// Program.cs
// Console Snake Game - AI prompts adjusted to force the model to output a single short sentence (no explanation).
// Usage: .NET 6+
// 注意：为简便示例，API Key 仍写在代码中。生产环境请使用环境变量或配置文件存储密钥。

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
            var mode = ShowStartMenu();
            var game = new Game(mode);
            await game.Start();
        }

        private static GameMode ShowStartMenu()
        {
            Console.CursorVisible = true;
            Console.Clear();
            Console.WriteLine("===== 贪吃蛇游戏 =====");
            Console.WriteLine("说明：方向键操作，Esc 退出。");
            Console.WriteLine();
            Console.WriteLine("1. 本地关卡 (随机生成食物)");
            Console.WriteLine("2. AI生成关卡 (AI决定食物位置)");
            Console.WriteLine();
            Console.Write("请选择模式 (1/2): ");

            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.KeyChar == '1') return GameMode.Local;
                if (key.KeyChar == '2') return GameMode.AI;
            }
        }
    }

    public enum GameMode { Local, AI }

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

        private int score;
        private int survivalTime;
        private int maxSnakeLength;
        private DateTime startTime;
        private string collisionReason = "";

        // persistent AI message (won't be lost by Console.Clear as we reprint it)
        private string lastAiMessage = "";

        public Game(GameMode mode)
        {
            gameMode = mode;
            aiService = new AIService();

            int bufW = Math.Max(20, Console.BufferWidth);
            int bufH = Math.Max(25, Console.BufferHeight);

            displayWidth = Math.Min(LogicalBoundary, Math.Max(10, bufW - 2));
            displayHeight = Math.Min(LogicalBoundary, Math.Max(10, bufH - 6));

            infoLine = displayHeight + 1;
            aiPersistentLine = displayHeight + 2;
            summaryStartLine = displayHeight + 4;

            snake = new Snake(new Position(10, 10));
            score = 0;
            survivalTime = 0;
            maxSnakeLength = 1;

            GenerateFood().Wait();
        }

        public async Task Start()
        {
            Console.CursorVisible = false;
            startTime = DateTime.Now;

            // 1) AI at game start — request a single short sentence (no explanation)
            try
            {
                string startPrompt = $"游戏开始。模式={gameMode}。请只输出一句中文鼓励或实用提示（不要解释、不要多余文本、不要引号）。";
                lastAiMessage = await aiService.GetInteractionOnceAsync(startPrompt);
            }
            catch
            {
                lastAiMessage = "[AI 暂不可用]";
            }

            var inputTask = Task.Run(() => ListenForInput());

            while (true)
            {
                lock (_consoleLock)
                {
                    Console.Clear();
                    DrawBoundary();
                    DisplayGame();
                    PrintPersistentAiLine(); // ensure persistent AI message reprinted each frame
                }

                snake.Move();

                if (snake.Head.X == food.X && snake.Head.Y == food.Y)
                {
                    score++;
                    snake.Grow();
                    maxSnakeLength = Math.Max(maxSnakeLength, snake.Body.Count);

                    // 2) AI on eating food — request a single short sentence (no explanation)
                    try
                    {
                        string state = $"玩家吃到食物。Score={score}, Time={(int)(DateTime.Now - startTime).TotalSeconds}s, Head=({snake.Head.X},{snake.Head.Y}). 请只输出一句中文鼓励或实用提示（不要解释、不要多余文本、不要引号）。";
                        string aiReply = await aiService.GetInteractionOnEatAsync(state);
                        lastAiMessage = aiReply;
                        lock (_consoleLock)
                        {
                            PrintPersistentAiLine(); // immediately show it
                        }
                    }
                    catch
                    {
                        lastAiMessage = "[AI 互动失败]";
                    }

                    await GenerateFood();
                }

                if (CheckCollisions())
                {
                    break;
                }

                survivalTime = (int)(DateTime.Now - startTime).TotalSeconds;

                Thread.Sleep(120);
            }

            // 3) AI at game over — detailed summary (can be multi-sentence)
            string summaryPrompt = $"玩家本局得分 {score}，存活 {survivalTime} 秒，最大蛇身长度 {maxSnakeLength}，碰撞原因：{collisionReason}。请生成：1) 一句本局总结（简明具体），2) 一条长期训练建议。返回纯文本。";
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
                SafeSetCursor(0, 0);
                Console.WriteLine("===== 游戏结束 =====");
                Console.WriteLine($"最终得分: {score}");
                Console.WriteLine($"存活时间: {survivalTime} 秒");
                Console.WriteLine($"最大蛇身长度: {maxSnakeLength}");
                Console.WriteLine($"碰撞原因: {collisionReason}");
                Console.WriteLine();
                Console.WriteLine("----- AI 复盘建议 -----");
                Console.WriteLine(aiSummary);
                Console.WriteLine();
                Console.WriteLine("按任意键退出...");
            }

            Console.ReadKey(true);
            Environment.Exit(0);
        }

        private void SafeSetCursor(int x, int y)
        {
            int maxX = Math.Max(0, Console.BufferWidth - 1);
            int maxY = Math.Max(0, Console.BufferHeight - 1);
            int cx = Math.Clamp(x, 0, maxX);
            int cy = Math.Clamp(y, 0, maxY);
            Console.SetCursorPosition(cx, cy);
        }

        private void DrawBoundary()
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

        private void PrintPersistentAiLine()
        {
            SafeSetCursor(0, aiPersistentLine);
            int width = Math.Max(0, Console.WindowWidth - 1);
            Console.Write(new string(' ', width));
            SafeSetCursor(0, aiPersistentLine);
            string show = lastAiMessage ?? "";
            if (show.Length > width - 4) show = show.Substring(0, width - 7) + "...";
            Console.Write("AI: " + show);
        }

        private void DisplayGame()
        {
            foreach (var p in snake.Body)
            {
                if (IsInsideDisplayArea(p))
                {
                    SafeSetCursor(p.X, p.Y);
                    Console.Write("■");
                }
            }

            if (IsInsideDisplayArea(food))
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

        private void ListenForInput()
        {
            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var k = Console.ReadKey(true);
                    switch (k.Key)
                    {
                        case ConsoleKey.UpArrow:
                            if (snake.CurrentDirection != Direction.Down) snake.CurrentDirection = Direction.Up;
                            break;
                        case ConsoleKey.DownArrow:
                            if (snake.CurrentDirection != Direction.Up) snake.CurrentDirection = Direction.Down;
                            break;
                        case ConsoleKey.LeftArrow:
                            if (snake.CurrentDirection != Direction.Right) snake.CurrentDirection = Direction.Left;
                            break;
                        case ConsoleKey.RightArrow:
                            if (snake.CurrentDirection != Direction.Left) snake.CurrentDirection = Direction.Right;
                            break;
                        case ConsoleKey.Escape:
                            Environment.Exit(0);
                            break;
                    }
                }
                Thread.Sleep(8);
            }
        }

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
                collisionReason = "撞到边界";
                return true;
            }
            if (snake.Body.Skip(1).Any(p => p.X == snake.Head.X && p.Y == snake.Head.Y))
            {
                collisionReason = "撞到自己";
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
            Body.RemoveAt(Body.Count - 1);
        }

        public void Grow()
        {
            Body.Add(Body.Last());
        }
    }

    public struct Position { public int X; public int Y; public Position(int x, int y) { X = x; Y = y; } }
    public enum Direction { Up, Down, Left, Right }

    public class AIService
    {
        private readonly string apiKey = "6b11364e-8591-40bd-b257-7b5c0e0b8653";
        private readonly string endpoint = "https://ark.cn-beijing.volces.com/api/v3/chat/completions";
        private readonly HttpClient client;

        public AIService()
        {
            client = new HttpClient();
        }

        // Start prompt: force direct single-sentence output
        public async Task<string> GetInteractionOnceAsync(string stateSummary)
        {
            var body = new
            {
                model = "doubao-seed-1-6-251015",
                max_completion_tokens = 80,
                messages = new object[]
                {
                    new {
                        role = "user",
                        content = new object[]
                        {
                            new { text = $"{stateSummary} 请只输出一句中文鼓励或实用提示，**不要解释、不用展示思考过程、不要多余文本、不要引号**。", type = "text" }
                        }
                    }
                },
                reasoning_effort = "low"
            };
            return await PostAndExtractSingleLineAsync(body);
        }

        // On-eat prompt: force direct single-sentence output
        public async Task<string> GetInteractionOnEatAsync(string stateSummary)
        {
            var body = new
            {
                model = "doubao-seed-1-6-251015",
                max_completion_tokens = 80,
                messages = new object[]
                {
                    new {
                        role = "user",
                        content = new object[]
                        {
                            new { text = $"{stateSummary} 请只输出一句中文鼓励或实用提示，**不要解释、不用展示思考过程、不要多余文本、不要引号**。", type = "text" }
                        }
                    }
                },
                reasoning_effort = "low"
            };
            return await PostAndExtractSingleLineAsync(body);
        }

        // Generate food position (unchanged)
        public async Task<string> GetFoodPositionAsync(List<Position> snakeBody)
        {
            string snakePositions = string.Join(";", snakeBody.Select(p => $"({p.X},{p.Y})"));
            var body = new
            {
                model = "doubao-seed-1-6-251015",
                max_completion_tokens = 200,
                messages = new object[]
                {
                    new {
                        role = "user",
                        content = new object[]
                        {
                            new { text = $"贪吃蛇当前位置：{snakePositions}。请在1到19之间生成一个食物位置，格式 'X:数字,Y:数字' 或 '数字,数字'，只返回位置，不要其他说明。", type = "text" }
                        }
                    }
                },
                reasoning_effort = "medium"
            };
            return await PostAndExtractAsync(body);
        }

        // Generate summary at gameover (can be multi-sentence)
        public async Task<string> GenerateSummaryAsync(string prompt)
        {
            var body = new
            {
                model = "doubao-seed-1-6-251015",
                max_completion_tokens = 800,
                messages = new object[]
                {
                    new {
                        role = "user",
                        content = new object[]
                        {
                            new { text = prompt, type = "text" }
                        }
                    }
                },
                reasoning_effort = "medium"
            };
            return await PostAndExtractAsync(body);
        }

        // Post and extract single-line response: prefer a single short string (no explanation)
        private async Task<string> PostAndExtractSingleLineAsync(object body)
        {
            try
            {
                string payload = System.Text.Json.JsonSerializer.Serialize(body);
                var content = new StringContent(payload, Encoding.UTF8, "application/json");
                var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
                req.Headers.Add("Authorization", $"Bearer {apiKey}");

                var resp = await client.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                var raw = await resp.Content.ReadAsStringAsync();

                // Try to extract likely answer string
                var extracted = ExtractTextFromApiResponse(raw).Trim();

                // If extracted contains multiple sentences or long explanation, take first sentence/line
                if (string.IsNullOrWhiteSpace(extracted)) return "";
                // Split by common sentence delimiters, prefer the first non-empty
                var candidates = extracted.Split(new[] { '\n', '.', '。', '!', '！' }, StringSplitOptions.RemoveEmptyEntries)
                                          .Select(s => s.Trim())
                                          .Where(s => s.Length > 0)
                                          .ToArray();
                if (candidates.Length > 0)
                {
                    // Return first candidate as the single-line message
                    string one = candidates[0];
                    // Remove surrounding quotes if present
                    if ((one.StartsWith("\"") && one.EndsWith("\"")) || (one.StartsWith("“") && one.EndsWith("”")))
                    {
                        one = one.Substring(1, one.Length - 2);
                    }
                    // Final safety: ensure not too long
                    if (one.Length > 120) one = one.Substring(0, 117) + "...";
                    return one;
                }

                // fallback to truncated raw
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
                string payload = System.Text.Json.JsonSerializer.Serialize(body);
                var content = new StringContent(payload, Encoding.UTF8, "application/json");
                var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
                req.Headers.Add("Authorization", $"Bearer {apiKey}");

                var resp = await client.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                var raw = await resp.Content.ReadAsStringAsync();
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
                string best = "";
                void Walk(System.Text.Json.JsonElement el)
                {
                    switch (el.ValueKind)
                    {
                        case System.Text.Json.JsonValueKind.String:
                            var s = el.GetString();
                            if (!string.IsNullOrWhiteSpace(s) && s.Length > best.Length) best = s;
                            break;
                        case System.Text.Json.JsonValueKind.Object:
                            foreach (var p in el.EnumerateObject()) Walk(p.Value);
                            break;
                        case System.Text.Json.JsonValueKind.Array:
                            foreach (var e in el.EnumerateArray()) Walk(e);
                            break;
                        default: break;
                    }
                }
                Walk(doc.RootElement);
                if (!string.IsNullOrWhiteSpace(best)) return best;
            }
            catch { /* not json or parse failed */ }

            var cleaned = raw.Replace("\r", " ").Replace("\n", " ").Trim();
            if (cleaned.Length > 2000) cleaned = cleaned.Substring(0, 2000) + "...";
            return cleaned;
        }
    }
}
