// Program.cs
// 修复版：在控制台尺寸较小的情况下避免 SetCursorPosition 引发 ArgumentOutOfRangeException
// 功能：控制台贪吃蛇 + AI 每3s 互动 + 游戏结束调用 LLM 生成复盘建议
// 说明：请在运行前确保控制台窗口至少较为宽敞（推荐宽 >= 40，高 >= 25），
//      若窗口太小程序会提示并等待你放大窗口后再继续。

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
            Console.OutputEncoding = Encoding.UTF8; // 保证方块字符显示
            GameMode mode = ShowStartMenu();
            Game game = new Game(mode);
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
        // 期望的逻辑边界（逻辑格数）
        private const int BoundarySize = 20;

        // 实际用于绘制/计算的宽高（取决于控制台缓冲区大小）
        private readonly int displayWidth;
        private readonly int displayHeight;

        // 屏幕显示相关行号（在 displayHeight 基础上决定）
        private readonly int infoLine;
        private readonly int aiLine;
        private readonly int bottomLine;

        // 同步锁，避免多线程写屏冲突
        private readonly object _consoleLock = new object();

        private Snake snake;
        private Position food;
        private readonly AIService aiService;
        private readonly GameMode gameMode;

        // 游戏统计
        private int score;
        private int survivalTime;
        private int maxSnakeLength;
        private DateTime startTime;
        private string collisionReason = "";

        // 控制 AI 互动任务取消
        private CancellationTokenSource ctsAiInteraction;

        public Game(GameMode mode)
        {
            // 读取控制台缓冲区大小并决定实际的显示尺寸，预留若干行给信息显示
            // 注意：Console.BufferHeight/Width 取决于运行环境，部分终端可能无法设置很大
            int bufW = Math.Max(20, Console.BufferWidth);
            int bufH = Math.Max(25, Console.BufferHeight);

            // 实际显示宽高不能超过缓冲区 - 1（safe），并且不大于 BoundarySize。
            // displayWidth 用作 X 的最大索引（边框将绘制在 0 和 displayWidth）
            displayWidth = Math.Min(BoundarySize, Math.Max(10, bufW - 2));
            // displayHeight 需要留出若干行给脚注（infoLine/aiLine），所以从缓冲高度减掉 6
            displayHeight = Math.Min(BoundarySize, Math.Max(10, bufH - 6));

            // 计算显示信息行（确保在缓冲区范围内）
            infoLine = displayHeight + 1;
            aiLine = displayHeight + 2;
            bottomLine = displayHeight + 4;

            // If the console is too small, prompt the user to enlarge it to avoid runtime exceptions
            // 推荐尺寸：宽 >= 30，高 >= 20
            if (Console.WindowWidth < 30 || Console.WindowHeight < 20)
            {
                Console.Clear();
                Console.WriteLine("检测到控制台窗口较小。为确保游戏正常显示，请将控制台窗口放大后按任意键继续。");
                Console.WriteLine("推荐最小尺寸：宽 >= 30， 高 >= 20（更大更好）。");
                Console.WriteLine();
                Console.WriteLine($"当前缓冲区尺寸 BufferWidth={Console.BufferWidth}, BufferHeight={Console.BufferHeight}");
                Console.WriteLine("按任意键继续（若未放大窗口，程序可能仍然受限）...");
                Console.ReadKey(true);

                // 重新计算基于新的缓冲区
                bufW = Math.Max(20, Console.BufferWidth);
                bufH = Math.Max(25, Console.BufferHeight);
                displayWidth = Math.Min(BoundarySize, Math.Max(10, bufW - 2));
                displayHeight = Math.Min(BoundarySize, Math.Max(10, bufH - 6));
                // recompute lines
                // Note: These fields are readonly, but we are in constructor; reassigning is OK.
            }

            // 初始化游戏实体
            gameMode = mode;
            snake = new Snake(new Position(10, 10));
            aiService = new AIService();
            score = 0;
            survivalTime = 0;
            maxSnakeLength = 1;

            // 生成第一颗食物（异步调用在构造中同步等待，以保证游戏开始时已有食物）
            GenerateFood().Wait();
        }

        // 游戏主循环
        public async Task Start()
        {
            Console.CursorVisible = false;
            ctsAiInteraction = new CancellationTokenSource();

            // 启动输入监听（方向键）
            var inputTask = Task.Run(() => ListenForInput());

            // 启动 AI 周期性互动任务（每 3 秒）
            var aiTask = Task.Run(() => AIInteractionLoop(ctsAiInteraction.Token));

            startTime = DateTime.Now;

            while (true)
            {
                lock (_consoleLock)
                {
                    Console.Clear();
                    DrawBoundary();
                    DisplayGame();
                }

                snake.Move();

                // 吃到食物
                if (snake.Head.X == food.X && snake.Head.Y == food.Y)
                {
                    score++;
                    snake.Grow(); // 正确增长：在吃到食物时增长身体长度
                    maxSnakeLength = Math.Max(maxSnakeLength, snake.Body.Count);
                    await GenerateFood();
                }

                // 检查碰撞（基于 displayWidth/displayHeight）
                if (CheckCollisions())
                {
                    break; // 结束主循环 -> 游戏结束
                }

                survivalTime = (int)(DateTime.Now - startTime).TotalSeconds;

                // 非阻塞地向 AI 请求提示（忽略返回）
                _ = aiService.GetHintAsync($"Snake at ({snake.Head.X},{snake.Head.Y}), Food at ({food.X},{food.Y})")
                    .ContinueWith(t => { /* 如果需要，可记录日志 */ });

                Thread.Sleep(120); // 控制帧率
            }

            // 停止 AI 循环
            ctsAiInteraction.Cancel();

            // 显示结束与 AI 复盘
            await DisplayGameOverAndSummary();
        }

        // 在绘制 Cursor 前进行 Clamp，避免越界抛异常
        private void SafeSetCursor(int x, int y)
        {
            int maxX = Math.Max(0, Console.BufferWidth - 1);
            int maxY = Math.Max(0, Console.BufferHeight - 1);
            int cx = Math.Clamp(x, 0, maxX);
            int cy = Math.Clamp(y, 0, maxY);
            Console.SetCursorPosition(cx, cy);
        }

        // 绘制边界（使用 displayWidth / displayHeight）
        private void DrawBoundary()
        {
            int w = displayWidth;
            int h = displayHeight;

            // 上下边界：x 从 0 到 w
            for (int x = 0; x <= w; x++)
            {
                SafeSetCursor(x, 0); Console.Write("#");
                SafeSetCursor(x, h); Console.Write("#");
            }

            // 左右边界：y 从 1 到 h-1
            for (int y = 1; y < h; y++)
            {
                SafeSetCursor(0, y); Console.Write("#");
                SafeSetCursor(w, y); SafeSetCursor(w, y); Console.Write("#");
            }
        }

        // 监听键盘输入，更新 snake.CurrentDirection
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

        // 在屏幕上绘制蛇、食物和底部信息
        private void DisplayGame()
        {
            // 绘制蛇
            foreach (var p in snake.Body)
            {
                if (IsInsideDisplayArea(p))
                {
                    SafeSetCursor(p.X, p.Y);
                    Console.Write("■");
                }
            }

            // 绘制食物
            if (IsInsideDisplayArea(food))
            {
                SafeSetCursor(food.X, food.Y);
                Console.Write("●");
            }

            // 底部信息行：Score / Time / MaxLen
            SafeSetCursor(0, displayHeight + 1);
            // 清空行（简单实现：写空格直到窗口宽度）
            Console.Write(new string(' ', Math.Max(0, Console.WindowWidth - 1)));
            SafeSetCursor(0, displayHeight + 1);
            Console.Write($"Score: {score}   Time: {survivalTime}s   MaxLen: {maxSnakeLength}");
        }

        // 返回是否在可显示区域内（不在边框上）
        private bool IsInsideDisplayArea(Position p)
        {
            return p.X > 0 && p.X < displayWidth && p.Y > 0 && p.Y < displayHeight;
        }

        // 生成食物（AI 模式或 本地随机）
        private async Task GenerateFood()
        {
            if (gameMode == GameMode.AI)
            {
                try
                {
                    string aiResponse = await aiService.GetFoodPositionAsync(snake.Body);
                    if (TryParsePosition(aiResponse, out Position aiFood) && IsValidPosition(aiFood))
                    {
                        food = aiFood;
                        return;
                    }
                }
                catch
                {
                    // 回退到本地随机
                }
            }

            // 本地随机生成
            Random rand = new Random(Guid.NewGuid().GetHashCode());
            Position candidate;
            do
            {
                candidate = new Position(rand.Next(1, displayWidth), rand.Next(1, displayHeight));
            } while (!IsValidPosition(candidate));
            food = candidate;
        }

        // 位置有效性：在 display 区域内且不在蛇身上
        private bool IsValidPosition(Position pos)
        {
            return pos.X > 0 && pos.X < displayWidth && pos.Y > 0 && pos.Y < displayHeight &&
                   !snake.Body.Any(p => p.X == pos.X && p.Y == pos.Y);
        }

        // 解析 AI 返回的可能位置格式（X:5,Y:3 / 5,3 / ...）
        private bool TryParsePosition(string input, out Position pos)
        {
            pos = new Position(0, 0);
            if (string.IsNullOrWhiteSpace(input)) return false;

            try
            {
                var t = input.Replace("\"", "").Replace("{", " ").Replace("}", " ").Replace("\n", " ").Replace("\r", " ");
                // 优先寻找 X: ... Y:
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

                // 尝试寻找两个连续的数字
                var parts = t.Split(new char[] { ' ', ',', ';', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
                             .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    if (int.TryParse(parts[i], out int a) && int.TryParse(parts[i + 1], out int b))
                    {
                        pos = new Position(a, b);
                        return true;
                    }
                }
            }
            catch
            {
                // 忽略解析异常
            }

            return false;
        }

        // 检查碰撞（基于 displayWidth/displayHeight）
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

        // 每 3 秒与 AI 互动一次（鼓励/短评），并在 aiLine 显示（不会抛出异常）
        private async Task AIInteractionLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    string stateSummary = $"Score:{score}, Time:{(int)(DateTime.Now - startTime).TotalSeconds}s, Head:({snake.Head.X},{snake.Head.Y}), Food:({food.X},{food.Y}), Len:{snake.Body.Count}";
                    string aiReply = await aiService.GetInteractionAsync(stateSummary);

                    lock (_consoleLock)
                    {
                        // 清空 aiLine 并写入（保证不超出缓冲区）
                        int maxY = Math.Max(0, Console.BufferHeight - 1);
                        int targetLine = Math.Min(aiLine, maxY);
                        SafeSetCursor(0, targetLine);
                        Console.Write(new string(' ', Math.Max(0, Console.WindowWidth - 1)));
                        SafeSetCursor(0, targetLine);
                        string show = aiReply;
                        if (show.Length > Console.WindowWidth - 6) show = show.Substring(0, Console.WindowWidth - 6) + "...";
                        Console.Write($"AI: {show}");
                    }
                }
                catch
                {
                    // 忽略 AI 异常
                }

                try
                {
                    await Task.Delay(3000, token);
                }
                catch (TaskCanceledException) { break; }
            }
        }

        // 游戏结束后调用 AI 生成复盘与建议并显示
        private async Task DisplayGameOverAndSummary()
        {
            string prompt = $"玩家本局得分 {score}，存活 {survivalTime} 秒，最大蛇身长度 {maxSnakeLength}，碰撞原因：{collisionReason}。请生成：1) 一句本局总结（具体）、2) 一条长期训练建议。返回纯文本。";

            string aiSummary;
            try
            {
                aiSummary = await aiService.GenerateSummaryAsync(prompt);
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
    }

    // 蛇类
    public class Snake
    {
        public List<Position> Body { get; set; }
        public Position Head => Body.First();
        public Direction CurrentDirection { get; set; }

        public Snake(Position spawnPosition)
        {
            Body = new List<Position> { spawnPosition };
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

    public struct Position
    {
        public int X;
        public int Y;
        public Position(int x, int y) { X = x; Y = y; }
    }

    public enum Direction { Up, Down, Left, Right }

    // AIService: 与 LLM 交互（示例）
    public class AIService
    {
        private readonly string apiKey = "6b11364e-8591-40bd-b257-7b5c0e0b8653";
        private readonly string endpoint = "https://ark.cn-beijing.volces.com/api/v3/chat/completions";
        private readonly HttpClient client;

        public AIService()
        {
            client = new HttpClient();
        }

        public async Task<string> GetHintAsync(string gameState)
        {
            var body = new
            {
                model = "doubao-seed-1-6-251015",
                max_completion_tokens = 512,
                messages = new object[]
                {
                    new {
                        role = "user",
                        content = new object[]
                        {
                            new { text = $"当前游戏状态：{gameState}，请简要建议下一步移动（上/下/左/右）", type = "text" }
                        }
                    }
                },
                reasoning_effort = "medium"
            };
            return await PostAndExtractAsync(body);
        }

        public async Task<string> GetFoodPositionAsync(List<Position> snakeBody)
        {
            string snakePositions = string.Join(";", snakeBody.Select(p => $"({p.X},{p.Y})"));
            var body = new
            {
                model = "doubao-seed-1-6-251015",
                max_completion_tokens = 256,
                messages = new object[]
                {
                    new {
                        role = "user",
                        content = new object[]
                        {
                            new { text = $"贪吃蛇当前位置：{snakePositions}，请在1到19之间（包含1和19）生成一个食物位置，格式为'X:数字,Y:数字'，不要其他内容。", type = "text" }
                        }
                    }
                },
                reasoning_effort = "medium"
            };
            return await PostAndExtractAsync(body);
        }

        public async Task<string> GetInteractionAsync(string stateSummary)
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
                            new { text = $"当前状态：{stateSummary}。请用一句话鼓励/评价玩家（不超过一句）", type = "text" }
                        }
                    }
                },
                reasoning_effort = "low"
            };
            return await PostAndExtractAsync(body);
        }

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

        // 统一 POST 调用 & 简单文本抽取（尽量返回有意义文本）
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

        private string ExtractTextFromApiResponse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";

            // 尝试解析 JSON 并提取第一个较长的字符串字段
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
            catch
            {
                // 非 json 或解析失败：继续执行下面清理流程
            }

            var cleaned = raw.Replace("\r", " ").Replace("\n", " ").Trim();
            if (cleaned.Length > 2000) cleaned = cleaned.Substring(0, 2000) + "...";
            return cleaned;
        }
    }
}
