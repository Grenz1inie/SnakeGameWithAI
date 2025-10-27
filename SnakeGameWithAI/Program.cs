// Program.cs
// Console Snake Game with AI calls only at:
//  1) game start
//  2) when the snake eats food
//  3) at game over (detailed summary)
// AI messages written to a persistent AI line that is reprinted every frame so they won't be lost.

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
        // Logical grid size (maximum). Actual drawing clamps to console buffer.
        private const int LogicalBoundary = 20;

        // Actual drawing extents based on console buffer/window to avoid out-of-range
        private int displayWidth;
        private int displayHeight;

        // Lines reserved for info and AI persistent message and final summary
        private int infoLine;          // score/time line
        private int aiPersistentLine;  // persistent AI interaction line (re-drawn every frame)
        private int summaryStartLine;  // where final summary is printed

        private readonly object _consoleLock = new object();

        private Snake snake;
        private Position food;
        private readonly AIService aiService;
        private readonly GameMode gameMode;

        // Stats
        private int score;
        private int survivalTime;
        private int maxSnakeLength;
        private DateTime startTime;
        private string collisionReason = "";

        // Last persistent AI message (set at start and on each food-eaten event)
        private string lastAiMessage = "";

        public Game(GameMode mode)
        {
            gameMode = mode;
            aiService = new AIService();

            // Initialize display dimensions safely based on current buffer/window
            // Reserve a few lines at bottom for info and summary so we don't draw outside buffer.
            int bufW = Math.Max(20, Console.BufferWidth);
            int bufH = Math.Max(25, Console.BufferHeight);

            // clamp to logical boundary, and leave lines for info
            displayWidth = Math.Min(LogicalBoundary, Math.Max(10, bufW - 2));
            displayHeight = Math.Min(LogicalBoundary, Math.Max(10, bufH - 6));

            infoLine = displayHeight + 1;
            aiPersistentLine = displayHeight + 2;
            summaryStartLine = displayHeight + 4;

            snake = new Snake(new Position(10, 10));
            score = 0;
            survivalTime = 0;
            maxSnakeLength = 1;

            // initial food
            GenerateFood().Wait();
        }

        // Main startup + loop
        public async Task Start()
        {
            Console.CursorVisible = false;
            startTime = DateTime.Now;

            // 1) Call AI once at game start (welcome / small tip)
            try
            {
                string startPrompt = $"游戏开始：模式={gameMode}. 初始蛇头=({snake.Head.X},{snake.Head.Y}), 边界=1..{displayWidth - 1}";
                lastAiMessage = await aiService.GetInteractionOnceAsync(startPrompt);
            }
            catch
            {
                lastAiMessage = "[AI 暂不可用 - 使用本地提示]";
            }

            // Main loop variables
            var inputTask = Task.Run(() => ListenForInput()); // keyboard listener
            while (true)
            {
                lock (_consoleLock)
                {
                    Console.Clear();                     // clear whole screen but we will immediately re-print the AI persistent line
                    DrawBoundary();
                    DisplayGame();                       // draws snake & food & info
                    // Reprint the persistent AI message AFTER clearing so it stays visible
                    PrintPersistentAiLine();
                }

                snake.Move();

                // If eat food -> grow, update score, call AI once for feedback (and set lastAiMessage)
                if (snake.Head.X == food.X && snake.Head.Y == food.Y)
                {
                    score++;
                    snake.Grow();
                    maxSnakeLength = Math.Max(maxSnakeLength, snake.Body.Count);

                    // call AI to get small immediate feedback and update persistent line (only now)
                    try
                    {
                        string state = $"At eat: Score={score}, Time={(int)(DateTime.Now - startTime).TotalSeconds}s, Head=({snake.Head.X},{snake.Head.Y}), Len={snake.Body.Count}";
                        string aiReply = await aiService.GetInteractionOnceAsync(state);
                        // store persistent message and ensure it's displayed next frame (we also print immediately)
                        lastAiMessage = aiReply;
                        lock (_consoleLock)
                        {
                            // Immediately print (so player sees instant feedback without waiting next frame)
                            PrintPersistentAiLine();
                        }
                    }
                    catch
                    {
                        lastAiMessage = "[AI 互动失败]";
                    }

                    await GenerateFood();
                }

                // Check collisions
                if (CheckCollisions())
                {
                    break;
                }

                survivalTime = (int)(DateTime.Now - startTime).TotalSeconds;

                // Minor delay
                Thread.Sleep(120);
            }

            // Game over - call AI for detailed summary (one call)
            string summaryPrompt = $"玩家本局得分 {score}，存活 {survivalTime} 秒，最大蛇身长度 {maxSnakeLength}，碰撞原因：{collisionReason}。请生成：1) 一句简短的本局总结，2) 一条长期训练建议。返回纯文本。";
            string aiSummary;
            try
            {
                aiSummary = await aiService.GenerateSummaryAsync(summaryPrompt);
            }
            catch (Exception ex)
            {
                aiSummary = $"AI 复盘失败：{ex.Message}";
            }

            // Display final summary on reserved area
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

        // Helper: safely set cursor without ArgumentOutOfRange
        private void SafeSetCursor(int x, int y)
        {
            int maxX = Math.Max(0, Console.BufferWidth - 1);
            int maxY = Math.Max(0, Console.BufferHeight - 1);
            int cx = Math.Clamp(x, 0, maxX);
            int cy = Math.Clamp(y, 0, maxY);
            Console.SetCursorPosition(cx, cy);
        }

        // Draw border using displayWidth/displayHeight
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

        // Print the persistent AI line (keeps it visible)
        private void PrintPersistentAiLine()
        {
            SafeSetCursor(0, aiPersistentLine);
            int width = Math.Max(0, Console.WindowWidth - 1);
            Console.Write(new string(' ', width)); // clear line
            SafeSetCursor(0, aiPersistentLine);
            string show = lastAiMessage ?? "";
            if (show.Length > width - 4) show = show.Substring(0, width - 7) + "...";
            Console.Write("AI: " + show);
        }

        // Display the snake, food, and info (score/time) - called every frame
        private void DisplayGame()
        {
            // Draw snake
            foreach (var p in snake.Body)
            {
                if (IsInsideDisplayArea(p))
                {
                    SafeSetCursor(p.X, p.Y);
                    Console.Write("■");
                }
            }

            // Draw food
            if (IsInsideDisplayArea(food))
            {
                SafeSetCursor(food.X, food.Y);
                Console.Write("●");
            }

            // Info line (score/time) - reprinted every frame
            SafeSetCursor(0, infoLine);
            int width = Math.Max(0, Console.WindowWidth - 1);
            Console.Write(new string(' ', width));
            SafeSetCursor(0, infoLine);
            Console.Write($"Score: {score}   Time: {survivalTime}s   MaxLen: {maxSnakeLength}");
        }

        // Listen for arrow keys
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

        // Generate food (AI mode asks AI, otherwise local random)
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
                catch { /* fallback to local */ }
            }

            // fallback local random
            Random rnd = new Random(Guid.NewGuid().GetHashCode());
            Position candidate;
            do
            {
                candidate = new Position(rnd.Next(1, displayWidth), rnd.Next(1, displayHeight));
            } while (!IsValidPosition(candidate));
            food = candidate;
        }

        // Check if position is inside playable area (not on border)
        private bool IsInsideDisplayArea(Position p)
        {
            return p.X > 0 && p.X < displayWidth && p.Y > 0 && p.Y < displayHeight;
        }

        // Valid position: inside and not on snake body
        private bool IsValidPosition(Position pos)
        {
            return pos.X > 0 && pos.X < displayWidth && pos.Y > 0 && pos.Y < displayHeight &&
                   !snake.Body.Any(b => b.X == pos.X && b.Y == pos.Y);
        }

        // Parse AI returned position (supports "X:5,Y:3" or "5,3" etc.)
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

        // Collision detection; returns true if game should end
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

    // Snake class: body list, move & grow
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

        // Grow by duplicating tail (next frame movement keeps the extra tail)
        public void Grow()
        {
            Body.Add(Body.Last());
        }
    }

    public struct Position { public int X; public int Y; public Position(int x, int y) { X = x; Y = y; } }
    public enum Direction { Up, Down, Left, Right }

    // AIService: single-instance HttpClient, methods used only at start / on-eat / on-gameover
    public class AIService
    {
        // NOTE: in production move key to env var or config
        private readonly string apiKey = "6b11364e-8591-40bd-b257-7b5c0e0b8653";
        private readonly string endpoint = "https://ark.cn-beijing.volces.com/api/v3/chat/completions";
        private readonly HttpClient client;

        public AIService()
        {
            client = new HttpClient();
        }

        // Called at start: one-time interaction
        public async Task<string> GetInteractionOnceAsync(string stateSummary)
        {
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
                            new { text = $"游戏开始。{stateSummary}。请用一句话鼓励玩家或给出小提示（不超过一句）", type = "text" }
                        }
                    }
                },
                reasoning_effort = "low"
            };
            return await PostAndExtractAsync(body);
        }

        // Called when snake eats food: one-time short feedback
        public async Task<string> GetInteractionOnEatAsync(string stateSummary)
        {
            var body = new
            {
                model = "doubao-seed-1-6-251015",
                max_completion_tokens = 180,
                messages = new object[]
                {
                    new {
                        role = "user",
                        content = new object[]
                        {
                            new { text = $"玩家吃到食物：{stateSummary}。请用一句话鼓励或简短点评（不超过一句）。", type = "text" }
                        }
                    }
                },
                reasoning_effort = "low"
            };
            return await PostAndExtractAsync(body);
        }

        // Called at gameover for detailed summary
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

        // Called by Game.GenerateFood when in AI mode to ask for a food position
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

        // Helper: POST and try to extract readable text from response
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

        // Try to pull a meaningful string from the API response (works for many JSON formats)
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

            // fallback: clean up whitespace and return truncated raw
            var cleaned = raw.Replace("\r", " ").Replace("\n", " ").Trim();
            if (cleaned.Length > 2000) cleaned = cleaned.Substring(0, 2000) + "...";
            return cleaned;
        }
    }
}
