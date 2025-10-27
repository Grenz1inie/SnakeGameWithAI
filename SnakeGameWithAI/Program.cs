// Program.cs
// Console Snake Game with AI interaction only at:
//  1) game start
//  2) when the snake eats food
//  3) at game over (detailed summary)
// Model/configuration for LLM is centralized at the top of AIService.
// Uses model "doubao-1-5-lite-32k-250115" per your request.
// .NET 6+ recommended.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SnakeGame
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            // Loop so after a game ends we return to the main menu
            var aiService = new AIService();
            while (true)
            {
                var mode = ShowStartMenu();
                var game = new Game(mode, aiService);
                await game.Start();
                // after Start returns, loop back to show menu again
            }
        }

        private static GameMode ShowStartMenu()
        {
            Console.CursorVisible = true;
            Console.Clear();
            Console.WriteLine("===== 贪吃蛇游戏 =====");
            Console.WriteLine("说明：下面列出本游戏支持的按键操作：");
            Console.WriteLine();
            Console.WriteLine("  - 方向键 (↑ ↓ ← →)：游戏中控制蛇移动");
            Console.WriteLine("  - Esc：在任意时刻退出游戏");
            Console.WriteLine("  - Space：暂停游戏");
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("1. 训练 (随机生成食物且没有障碍)");
            Console.WriteLine("2. AI生成关卡 (AI决定食物位置)");
            Console.WriteLine();
            Console.Write("请选择模式 (1/2): ");

            while (true)
            {
                var key = Console.ReadKey(true);
                // 在主菜单中按 Esc 退出程序
                if (key.Key == ConsoleKey.Escape) Environment.Exit(0);
                if (key.KeyChar == '1') return GameMode.Local;
                if (key.KeyChar == '2') return GameMode.AI;
            }
        }
    }

    public enum GameMode { Local, AI }

    // Holds the mutable game state
    public class GameState
    {
        public int Score { get; set; }
        public int SurvivalTime { get; set; }
        public int MaxSnakeLength { get; set; }
        public DateTime StartTime { get; set; }
        public bool Paused { get; private set; } = false;
        public DateTime? PauseStart { get; private set; } = null;
        public TimeSpan PausedTotal { get; private set; } = TimeSpan.Zero;
        public string CollisionReason { get; set; } = "";
        public string LastAiMessage { get; set; } = "";

        public void TogglePause()
        {
            Paused = !Paused;
            if (Paused)
            {
                PauseStart = DateTime.Now;
            }
            else
            {
                if (PauseStart.HasValue)
                {
                    PausedTotal += (DateTime.Now - PauseStart.Value);
                    PauseStart = null;
                }
            }
        }

        public int ComputeSurvivalSeconds()
        {
            var effectiveElapsed = DateTime.Now - StartTime - PausedTotal;
            return (int)effectiveElapsed.TotalSeconds;
        }
    }

    // Responsible for all console rendering
    public class ConsoleRenderer
    {
        private void SafeSetCursor(int x, int y)
        {
            int maxX = Math.Max(0, Console.BufferWidth - 1);
            int maxY = Math.Max(0, Console.BufferHeight - 1);
            int cx = Math.Clamp(x, 0, maxX);
            int cy = Math.Clamp(y, 0, maxY);
            Console.SetCursorPosition(cx, cy);
        }

        public void DrawBoundary(int displayWidth, int displayHeight)
        {
            int w = displayWidth;
            int h = displayHeight;
            for (int x = 0; x <= w; x++)
            {
                SafeSetCursor(x, 0); Console.Write("#");
                SafeSetCursor(x, h); Console.Write("#");
            }
            for (int y = 1; y < h; y++)
            {
                SafeSetCursor(0, y); Console.Write("#");
                SafeSetCursor(w, y); Console.Write("#");
            }
        }

        public void DisplayGame(List<Position> body, Position food, int displayWidth, int displayHeight, int infoLine, int score, int survivalTime, int maxSnakeLength)
        {
            foreach (var p in body)
            {
                if (p.X > 0 && p.X < displayWidth && p.Y > 0 && p.Y < displayHeight)
                {
                    SafeSetCursor(p.X, p.Y);
                    Console.Write("■");
                }
            }

            if (food.X > 0 && food.X < displayWidth && food.Y > 0 && food.Y < displayHeight)
            {
                SafeSetCursor(food.X, food.Y);
                Console.Write("●");
            }

            SafeSetCursor(0, infoLine);
            int width = Math.Max(0, Console.WindowWidth - 1);
            Console.Write(new string(' ', width));
            SafeSetCursor(0, infoLine);
            Console.Write($"Score: {score}   Time: {survivalTime}s   MaxLen: {maxSnakeLength}");
        }

        public void PrintPersistentAiLine(string lastAiMessage, int aiPersistentLine, bool paused)
        {
            SafeSetCursor(0, aiPersistentLine);
            int width = Math.Max(0, Console.WindowWidth - 1);
            Console.Write(new string(' ', width));
            SafeSetCursor(0, aiPersistentLine);
            string show = lastAiMessage ?? "";
            if (paused)
            {
                if (show.Length + 12 < width)
                    show = show + "    [已暂停 - 按 空格 继续]";
                else
                    show = "[已暂停 - 按 空格 继续]";
            }
            if (show.Length > width - 4) show = show.Substring(0, width - 7) + "...";
            Console.Write("AI: " + show);
        }

        public void RenderFrame(List<Position> body, Position food, int displayWidth, int displayHeight, int infoLine, int aiPersistentLine, string lastAiMessage, int score, int survivalTime, int maxSnakeLength, bool paused)
        {
            Console.Clear();
            DrawBoundary(displayWidth, displayHeight);
            DisplayGame(body, food, displayWidth, displayHeight, infoLine, score, survivalTime, maxSnakeLength);
            PrintPersistentAiLine(lastAiMessage, aiPersistentLine, paused);
        }
    }

    // Handles keyboard input and raises events
    public class InputHandler
    {
    public event Action<Direction>? DirectionChanged;
    public event Action? PauseToggled;
    public event Action? ExitRequested;

        private bool _running = true;

        public void Start()
        {
            Task.Run(() => RunLoop());
        }

        private void RunLoop()
        {
            while (_running)
            {
                if (Console.KeyAvailable)
                {
                    var k = Console.ReadKey(true);
                    switch (k.Key)
                    {
                        case ConsoleKey.Spacebar:
                            PauseToggled?.Invoke();
                            break;
                        case ConsoleKey.UpArrow:
                            DirectionChanged?.Invoke(Direction.Up);
                            break;
                        case ConsoleKey.DownArrow:
                            DirectionChanged?.Invoke(Direction.Down);
                            break;
                        case ConsoleKey.LeftArrow:
                            DirectionChanged?.Invoke(Direction.Left);
                            break;
                        case ConsoleKey.RightArrow:
                            DirectionChanged?.Invoke(Direction.Right);
                            break;
                        case ConsoleKey.Escape:
                            ExitRequested?.Invoke();
                            break;
                    }
                }
                Thread.Sleep(8);
            }
        }

        public void Stop() => _running = false;
    }

    public class Game
    {
        private const int LogicalBoundary = 20;
        private int displayWidth;
        private int displayHeight;
        private int infoLine;
        private int aiPersistentLine;
        private int summaryStartLine;

        private readonly object _consoleLock = new object();

        private Snake snake;
        private Position food;
        private readonly AIService aiService;
        private readonly GameMode gameMode;

        // composed helpers
        private readonly GameState state;
        private readonly ConsoleRenderer renderer;
        private readonly InputHandler inputHandler;
        // when true, user requested to return to main menu
        private volatile bool exitToMenuRequested = false;

        public Game(GameMode mode, AIService aiService)
        {
            gameMode = mode;
            this.aiService = aiService;

            state = new GameState();
            renderer = new ConsoleRenderer();
            inputHandler = new InputHandler();

            int bufW = Math.Max(20, Console.BufferWidth);
            int bufH = Math.Max(25, Console.BufferHeight);

            displayWidth = Math.Min(LogicalBoundary, Math.Max(10, bufW - 2));
            displayHeight = Math.Min(LogicalBoundary, Math.Max(10, bufH - 6));

            infoLine = displayHeight + 1;
            aiPersistentLine = displayHeight + 2;
            summaryStartLine = displayHeight + 4;

            snake = new Snake(new Position(10, 10));
            state.Score = 0;
            state.SurvivalTime = 0;
            state.MaxSnakeLength = 1;

            // hook input events
            inputHandler.DirectionChanged += (dir) => {
                lock (_consoleLock)
                {
                    switch (dir)
                    {
                        case Direction.Up:
                            if (snake.CurrentDirection != Direction.Down) snake.CurrentDirection = Direction.Up;
                            break;
                        case Direction.Down:
                            if (snake.CurrentDirection != Direction.Up) snake.CurrentDirection = Direction.Down;
                            break;
                        case Direction.Left:
                            if (snake.CurrentDirection != Direction.Right) snake.CurrentDirection = Direction.Left;
                            break;
                        case Direction.Right:
                            if (snake.CurrentDirection != Direction.Left) snake.CurrentDirection = Direction.Right;
                            break;
                    }
                }
            };
            inputHandler.PauseToggled += () => {
                lock (_consoleLock)
                {
                    state.TogglePause();
                    // immediate redraw
                    renderer.RenderFrame(snake.Body, food, displayWidth, displayHeight, infoLine, aiPersistentLine, state.LastAiMessage, state.Score, state.ComputeSurvivalSeconds(), state.MaxSnakeLength, state.Paused);
                }
            };
            // 在游戏中按 Esc 应当回到主菜单，而不是退出整个进程
            inputHandler.ExitRequested += () => {
                lock (_consoleLock)
                {
                    exitToMenuRequested = true;
                }
            };
            inputHandler.Start();

            GenerateFood().Wait();
        }

        public async Task Start()
        {
            Console.CursorVisible = false;
            state.StartTime = DateTime.Now;

            // 1) AI at game start — request a single short sentence (no explanation)
            try
            {
                string startPrompt = $"游戏开始。模式={gameMode}。请只输出几句中文鼓励或实用提示（不要解释、不要多余文本、不要引号）。";
                state.LastAiMessage = await aiService.GetInteractionOnceAsync(startPrompt);
            }
            catch
            {
                state.LastAiMessage = "[AI 暂不可用]";
            }

            // input handler already started in constructor

            while (true)
            {
                // 每帧渲染：计算当前生存时间以供渲染
                int currentSurvival = state.ComputeSurvivalSeconds();
                renderer.RenderFrame(snake.Body, food, displayWidth, displayHeight, infoLine, aiPersistentLine, state.LastAiMessage, state.Score, currentSurvival, state.MaxSnakeLength, state.Paused);

                if (exitToMenuRequested)
                {
                    // Stop input handler and return to menu without running game-over summary
                    inputHandler.Stop();
                    Console.CursorVisible = true;
                    return;
                }

                // Only move when not paused
                if (!state.Paused)
                {
                    snake.Move();
                }

                if (snake.Head.X == food.X && snake.Head.Y == food.Y)
                {
                    state.Score++;
                    snake.Grow();
                    state.MaxSnakeLength = Math.Max(state.MaxSnakeLength, snake.Body.Count);

                    // 2) AI on eating food — request a single short sentence (no explanation)
                    try
                    {
                        string prompt = $"玩家吃到食物。Score={state.Score}, Time={state.ComputeSurvivalSeconds()}s, Head=({snake.Head.X},{snake.Head.Y}). 请只输出几句中文鼓励或实用提示（不要解释、不要多余文本、不要引号）。";
                        string aiReply = await aiService.GetInteractionOnEatAsync(prompt);
                        state.LastAiMessage = aiReply;
                        lock (_consoleLock)
                        {
                            renderer.PrintPersistentAiLine(state.LastAiMessage, aiPersistentLine, state.Paused); // immediately show it
                        }
                    }
                        catch
                        {
                            state.LastAiMessage = "[AI 互动失败]";
                        }

                    await GenerateFood();
                }

                if (CheckCollisions())
                {
                    break;
                }

                // update survival time into state
                state.SurvivalTime = state.ComputeSurvivalSeconds();

                Thread.Sleep(120);
            }

            // 3) AI at game over — detailed summary (can be multi-sentence)
            // 停止输入监听线程（正常结束）
            inputHandler.Stop();
            string summaryPrompt = $"玩家本局得分 {state.Score}，存活 {state.SurvivalTime} 秒，最大蛇身长度 {state.MaxSnakeLength}，碰撞原因：{state.CollisionReason}。请生成：1) 几句本局总结（简明具体），2) 一条长期训练建议。返回纯文本。";
            string aiSummary;
            try
            {
                aiSummary = await aiService.GenerateSummaryAsync(summaryPrompt);
            }
            catch (Exception ex)
            {
                aiSummary = $"AI 复盘失败：{ex.Message}";
            }

            lock (_consoleLock)
            {
                Console.Clear();
                Console.WriteLine("===== 游戏结束 =====");
                Console.WriteLine($"最终得分: {state.Score}");
                Console.WriteLine($"存活时间: {state.SurvivalTime} 秒");
                Console.WriteLine($"最大蛇身长度: {state.MaxSnakeLength}");
                Console.WriteLine($"碰撞原因: {state.CollisionReason}");
                Console.WriteLine();
                Console.WriteLine("----- AI 复盘建议 -----");
                Console.WriteLine(aiSummary);
                Console.WriteLine();
                Console.WriteLine("按任意键退出...");
            }

            Console.ReadKey(true);
            // Return to caller (Main) so the main menu is shown again.
            return;
        }

        // input handled by InputHandler and rendering handled by ConsoleRenderer

        private async Task GenerateFood()
        {
            if (gameMode == GameMode.AI)
            {
                try
                {
                    string aiResp = await aiService.GetFoodPositionAsync(snake.Body);
                    if (TryParsePosition(aiResp, out Position aiFood) && IsValidPosition(aiFood))
                    {
                        food = aiFood;
                        return;
                    }
                }
                catch { /* fallback */ }
            }

            Random rnd = new Random(Guid.NewGuid().GetHashCode());
            Position candidate;
            do
            {
                candidate = new Position(rnd.Next(1, displayWidth), rnd.Next(1, displayHeight));
            } while (!IsValidPosition(candidate));
            food = candidate;
        }

        private bool IsInsideDisplayArea(Position p)
        {
            return p.X > 0 && p.X < displayWidth && p.Y > 0 && p.Y < displayHeight;
        }

        private bool IsValidPosition(Position pos)
        {
            return pos.X > 0 && pos.X < displayWidth && pos.Y > 0 && pos.Y < displayHeight &&
                   !snake.Body.Any(b => b.X == pos.X && b.Y == pos.Y);
        }

        private bool TryParsePosition(string input, out Position pos)
        {
            pos = new Position(0, 0);
            if (string.IsNullOrWhiteSpace(input)) return false;
            try
            {
                var t = input.Replace("\"", "").Replace("{", " ").Replace("}", " ").Replace("\n", " ").Replace("\r", " ");
                if (t.Contains("X:") && t.Contains("Y:"))
                {
                    int ix = t.IndexOf("X:");
                    int iy = t.IndexOf("Y:");
                    string sx = t.Substring(ix + 2).Split(new char[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)[0];
                    string sy = t.Substring(iy + 2).Split(new char[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)[0];
                    if (int.TryParse(sx, out int x) && int.TryParse(sy, out int y))
                    {
                        pos = new Position(x, y);
                        return true;
                    }
                }
                var parts = t.Split(new char[] { ' ', ',', ';', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    if (int.TryParse(parts[i], out int a) && int.TryParse(parts[i + 1], out int b))
                    {
                        pos = new Position(a, b);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private bool CheckCollisions()
        {
            if (snake.Head.X <= 0 || snake.Head.X >= displayWidth || snake.Head.Y <= 0 || snake.Head.Y >= displayHeight)
            {
                state.CollisionReason = "撞到边界";
                return true;
            }
            if (snake.Body.Skip(1).Any(p => p.X == snake.Head.X && p.Y == snake.Head.Y))
            {
                state.CollisionReason = "撞到自己";
                return true;
            }
            return false;
        }
    }

    public class Snake
    {
        public List<Position> Body { get; set; }
        public Position Head => Body.First();
        public Direction CurrentDirection { get; set; }
        // When true, the next Move() will grow the snake (i.e. not remove the tail)
        private bool pendingGrow = false;

        public Snake(Position spawn)
        {
            Body = new List<Position> { spawn };
            CurrentDirection = Direction.Right;
        }

        public void Move()
        {
            Position newHead = CurrentDirection switch
            {
                Direction.Up => new Position(Head.X, Head.Y - 1),
                Direction.Down => new Position(Head.X, Head.Y + 1),
                Direction.Left => new Position(Head.X - 1, Head.Y),
                Direction.Right => new Position(Head.X + 1, Head.Y),
                _ => Head
            };
            Body.Insert(0, newHead);
            // If a grow was requested, do not remove the tail this move (length increases by 1)
            if (pendingGrow)
            {
                pendingGrow = false;
            }
            else
            {
                Body.RemoveAt(Body.Count - 1);
            }
        }

        public void Grow()
        {
            // Mark that the snake should grow on the next Move() by preserving the tail
            // This avoids duplicating the head when the snake length is 1.
            pendingGrow = true;
        }
    }

    public struct Position { public int X; public int Y; public Position(int x, int y) { X = x; Y = y; } }
    public enum Direction { Up, Down, Left, Right }

    // AIService: centralized model/configuration and API calls
    public class AIService
    {
        // --- Unified model configuration (change here to switch model) ---
        private readonly string model = "doubao-1-5-lite-32k-250115";
        private readonly string apiKey = "6b11364e-8591-40bd-b257-7b5c0e0b8653";
        private readonly string endpoint = "https://ark.cn-beijing.volces.com/api/v3/chat/completions";
        // ----------------------------------------------------------------

        private readonly HttpClient client;

        public AIService()
        {
            client = new HttpClient();
        }

        // Called once at game start: force single-shot short reply
        public async Task<string> GetInteractionOnceAsync(string stateSummary)
        {
            // 系统提示加强：要求多样性、避免重复、限制长度并提供风格选项
            var body = BuildChatBody(
                ("system", "你是友好的游戏助手。每次回复要保持表达多样性，避免重复之前在同一局或同一会话中使用过的整句或固定短语。可在风格上随机选择：幽默、直率、温和或简洁，但每次仅输出几句中文（不超过30字），内容须是具体的鼓励或可执行的小建议。不要解释、不要额外文本、不要引号。"),
                ("user", stateSummary + " 请只输出几句中文鼓励或实用提示（<=30字），保持与之前不同的措辞或风格，不要解释、不要多余文本、不要引号.")
            );
            return await PostAndExtractSingleLineAsync(body);
        }

        // Called when snake eats food: one-shot short reply
        public async Task<string> GetInteractionOnEatAsync(string stateSummary)
        {
            // 吃到食物时的提示也应多样化，并可包含基于当前状态的小策略
            var body = BuildChatBody(
                ("system", "你是友好的游戏助手。吃到食物时要给出简短且多样化的几句中文提示（<=30字），可以是鼓励或基于当前局面的小策略。避免与本局之前的提示重复。不要解释、不要多余文本、不要引号。"),
                ("user", stateSummary + " 请只输出几句中文鼓励或简短策略（<=30字），措辞要与之前不同，不要解释、不要多余文本、不要引号.")
            );
            return await PostAndExtractSingleLineAsync(body);
        }

        // Ask AI for a food position (AI should return a simple position string)
        public async Task<string> GetFoodPositionAsync(List<Position> snakeBody)
        {
            string snakePositions = string.Join(";", snakeBody.Select(p => $"({p.X},{p.Y})"));
            var body = BuildChatBody(
                ("system", "你是游戏地图/关卡生成器。只返回坐标，不要说明。"),
                ("user", $"贪吃蛇当前位置：{snakePositions}。请在1到19之间生成一个食物位置，格式 'X:数字,Y:数字' 或 '数字,数字'，只返回位置，不要其他说明。")
            );
            return await PostAndExtractAsync(body);
        }

        // Generate a detailed summary at game over
        public async Task<string> GenerateSummaryAsync(string prompt)
        {
            // 要求复盘富有变化且具体：几句本局总结 + 若干可执行训练建议并标注优先级
            var body = BuildChatBody(
                ("system", "你是游戏教练。返回时请遵循：1) 用几句独特且具体的本局总结（避免通用模板和与之前重复的句子）；2) 给出2到3条可执行的长期训练建议，并为每条建议标注优先级（高/中/低）和简短原因。返回纯文本，结构清晰但不要包含多余闲话。"),
                ("user", prompt)
            );
            return await PostAndExtractAsync(body);
        }

        // Build a chat request body from role/content pairs
        private object BuildChatBody(params (string role, string content)[] messages)
        {
            var msgs = messages.Select(m => new { role = m.role, content = m.content }).ToArray();
            return new { model = model, messages = msgs };
        }

        // Centralized raw HTTP POST sender: returns raw response string
        private async Task<string> SendRequestRawAsync(object body)
        {
            string payload = System.Text.Json.JsonSerializer.Serialize(body);
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            req.Headers.Add("Authorization", $"Bearer {apiKey}");
            req.Headers.Add("Accept", "application/json");

            var resp = await client.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync();
        }

        // POST and extract single-line short response (first sentence / first non-empty line)
        private async Task<string> PostAndExtractSingleLineAsync(object body)
        {
            try
            {
                var raw = await SendRequestRawAsync(body);

                var extracted = ExtractTextFromApiResponse(raw).Trim();
                if (string.IsNullOrWhiteSpace(extracted)) return "开始游戏吧！";

                // 验证是否是合理的中文文本(至少包含一些中文字符)
                bool hasChineseChar = extracted.Any(c => c >= 0x4E00 && c <= 0x9FFF);
                // 过滤掉看起来像哈希值或ID的字符串(纯十六进制字符串)
                bool looksLikeHash = extracted.Length > 20 && 
                                    extracted.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
                
                if (looksLikeHash || !hasChineseChar)
                {
                    return "开始游戏吧！"; // 使用默认鼓励语
                }

                // Split into candidate lines/sentences, pick the first sensible one
                var candidates = extracted.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                                          .SelectMany(line => line.Split(new[] { '。', '.', '!', '！' }, StringSplitOptions.RemoveEmptyEntries))
                                          .Select(s => s.Trim())
                                          .Where(s => s.Length > 0)
                                          .ToArray();
                if (candidates.Length > 0)
                {
                    string one = candidates[0];
                    // Remove surrounding quotes if any
                    if ((one.StartsWith("\"") && one.EndsWith("\"")) || (one.StartsWith("“") && one.EndsWith("”")))
                        one = one.Substring(1, one.Length - 2);
                    if (one.Length > 120) one = one.Substring(0, 117) + "...";
                    return one;
                }

                // fallback: truncated raw
                if (extracted.Length > 120) return extracted.Substring(0, 117) + "...";
                return extracted;
            }
            catch (Exception ex)
            {
                return $"[AI 调用失败：{ex.Message}]";
            }
        }

        // General POST + extract (for position & summary)
        private async Task<string> PostAndExtractAsync(object body)
        {
            try
            {
                var raw = await SendRequestRawAsync(body);
                return ExtractTextFromApiResponse(raw);
            }
            catch (Exception ex)
            {
                return $"[AI 调用失败：{ex.Message}]";
            }
        }

        // Best-effort extract of readable text from possibly JSON responses
        private string ExtractTextFromApiResponse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                
                // 首先尝试标准的 OpenAI/豆包 API 格式: choices[0].message.content
                if (doc.RootElement.TryGetProperty("choices", out var choices) && 
                    choices.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var firstChoice = choices.EnumerateArray().FirstOrDefault();
                    if (firstChoice.ValueKind != System.Text.Json.JsonValueKind.Undefined &&
                        firstChoice.TryGetProperty("message", out var message) &&
                        message.TryGetProperty("content", out var content))
                    {
                        var text = content.GetString();
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                    }
                }
                
                // 备用方案：遍历所有字符串字段，但要排除 id、model 等元数据字段
                string best = "";
                HashSet<string> excludeKeys = new HashSet<string> { "id", "model", "object", "role", "finish_reason", "service_tier" };
                
                void Walk(System.Text.Json.JsonElement el, string currentKey = "")
                {
                    switch (el.ValueKind)
                    {
                        case System.Text.Json.JsonValueKind.String:
                            var s = el.GetString();
                            if (!string.IsNullOrWhiteSpace(s) && 
                                !excludeKeys.Contains(currentKey) &&
                                s.Length > best.Length) 
                            {
                                best = s;
                            }
                            break;
                        case System.Text.Json.JsonValueKind.Object:
                            foreach (var p in el.EnumerateObject()) 
                                Walk(p.Value, p.Name);
                            break;
                        case System.Text.Json.JsonValueKind.Array:
                            foreach (var e in el.EnumerateArray()) 
                                Walk(e, currentKey);
                            break;
                        default: break;
                    }
                }
                Walk(doc.RootElement);
                if (!string.IsNullOrWhiteSpace(best)) return best;
            }
            catch
            {
                // not JSON or parse failed -> fallback to raw cleaning
            }

            var cleaned = raw.Replace("\r", " ").Replace("\n", " ").Trim();
            if (cleaned.Length > 2000) cleaned = cleaned.Substring(0, 2000) + "...";
            return cleaned;
        }
    }
}
