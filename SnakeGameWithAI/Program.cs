// Program.cs
// Console Snake Game with AI interaction and post-game summary
// Requirements:
//  - .NET 6+ recommended
//  - Single-file console program (can be split into files for production)
//  - Uses basic HttpClient to call external LLM endpoints (example API shown in code)
//  - Very defensive: AI responses are parsed by heuristics; fallback behavior included

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SnakeGame
{
    // Entry point and simple menu
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8; // ensure box characters render

            // 显示开始界面并获取用户选择
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

    // 游戏模式
    public enum GameMode { Local, AI }

    // 游戏核心类
    public class Game
    {
        private const int BoundarySize = 20; // 边界矩阵大小（0..BoundarySize）
        private readonly object _consoleLock = new object();

        private Snake snake;
        private Position food;
        private readonly AIService aiService;
        private readonly GameMode gameMode;

        // 统计与复盘数据
        private int score;
        private int survivalTime; // seconds
        private int maxSnakeLength;
        private DateTime startTime;
        private string collisionReason = "";

        // 控制任务取消
        private CancellationTokenSource ctsAiInteraction;

        // 屏幕行号约定（避免覆盖游戏区域）
        private readonly int infoLine;     // 显示分数等（在游戏下方）
        private readonly int aiLine;       // AI 每 3s 的互动显示行
        private readonly int bottomLine;   // 结算/summary 显示起始行

        public Game(GameMode mode)
        {
            gameMode = mode;
            snake = new Snake(new Position(10, 10));
            aiService = new AIService(); // 可替换为 DI
            score = 0;
            survivalTime = 0;
            maxSnakeLength = 1;

            // 屏幕行配置（确保不覆盖边界 0..BoundarySize）
            infoLine = BoundarySize + 2;
            aiLine = BoundarySize + 3;
            bottomLine = BoundarySize + 5;

            // 初始化食物（若AI模式失败，会fallback为随机）
            GenerateFood().Wait();
        }

        // 启动游戏主循环
        public async Task Start()
        {
            Console.CursorVisible = false;
            ctsAiInteraction = new CancellationTokenSource();

            // 启动键盘监听
            var inputTask = Task.Run(() => ListenForInput());

            // 启动 AI 每 3s 的互动任务（不阻塞主循环）
            var aiInteractionTask = Task.Run(() => AIInteractionLoop(ctsAiInteraction.Token));

            startTime = DateTime.Now;

            // 游戏主循环
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
                    await GenerateFood(); // 生成新食物（AI 或 本地）
                }

                // 检查碰撞（边界或自身）
                if (CheckCollisions())
                {
                    break; // 若碰撞返回 true -> 游戏结束
                }

                // 更新时间
                survivalTime = (int)(DateTime.Now - startTime).TotalSeconds;

                // 可同时向 LLM 请求策略提示（非必须），但不阻塞主循环
                _ = aiService.GetHintAsync($"Snake at ({snake.Head.X},{snake.Head.Y}), Food at ({food.X},{food.Y})")
                    .ContinueWith(t =>
                    {
                        // 忽略结果或记录到日志；避免主循环被阻塞
                        if (t.IsFaulted) { /* 忽略 */ }
                    });

                Thread.Sleep(120); // 控制速度（ms）——可调整提高游戏难度
            }

            // 取消 AI 互动循环
            ctsAiInteraction.Cancel();

            // 游戏结束后生成复盘并显示
            await DisplayGameOverAndSummary();
        }

        // 绘制边界（#）
        private void DrawBoundary()
        {
            for (int x = 0; x <= BoundarySize; x++)
            {
                Console.SetCursorPosition(x, 0); Console.Write("#");
                Console.SetCursorPosition(x, BoundarySize); Console.Write("#");
            }
            for (int y = 1; y < BoundarySize; y++)
            {
                Console.SetCursorPosition(0, y); Console.Write("#");
                Console.SetCursorPosition(BoundarySize, y); Console.Write("#");
            }
        }

        // 监听键盘输入以改变蛇的方向
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

        // 显示蛇、食物以及简要信息（分数/时间）
        private void DisplayGame()
        {
            // 绘制蛇的每一个格子
            foreach (var p in snake.Body)
            {
                if (IsInsideDisplayArea(p))
                {
                    Console.SetCursorPosition(p.X, p.Y);
                    Console.Write("■");
                }
            }

            // 绘制食物
            if (IsInsideDisplayArea(food))
            {
                Console.SetCursorPosition(food.X, food.Y);
                Console.Write("●");
            }

            // 底部信息（分数、时间、长度）
            Console.SetCursorPosition(0, infoLine);
            Console.Write(new string(' ', Console.WindowWidth)); // 清空行
            Console.SetCursorPosition(0, infoLine);
            Console.Write($"Score: {score}   Time: {survivalTime}s   MaxLen: {maxSnakeLength}");
        }

        // 确保位置在边界内部（不画在边框上）
        private bool IsInsideDisplayArea(Position p)
        {
            return p.X > 0 && p.X < BoundarySize && p.Y > 0 && p.Y < BoundarySize;
        }

        // 生成食物：AI 模式或本地随机（包含回退）
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
                    // 忽略，走本地 fallback
                }
            }

            // 本地 fallback 随机生成
            Random rand = new Random(Guid.NewGuid().GetHashCode());
            Position newFood;
            do
            {
                newFood = new Position(rand.Next(1, BoundarySize), rand.Next(1, BoundarySize));
            } while (!IsValidPosition(newFood));
            food = newFood;
        }

        // 验证食物位置是否合理（不在蛇身、在边界内）
        private bool IsValidPosition(Position pos)
        {
            return pos.X > 0 && pos.X < BoundarySize && pos.Y > 0 && pos.Y < BoundarySize &&
                   !snake.Body.Any(p => p.X == pos.X && p.Y == pos.Y);
        }

        // 尝试从 AI 文本里解析 "X:数字,Y:数字" 或 "数字,数字" 等格式
        private bool TryParsePosition(string input, out Position pos)
        {
            pos = new Position(0, 0);
            if (string.IsNullOrWhiteSpace(input)) return false;

            // 尝试直接找到 "X:NN" 和 "Y:NN"
            try
            {
                var t = input.Replace("\"", "").Replace("{", "").Replace("}", "");
                // 找到 X: 与 Y:
                if (t.Contains("X:") && t.Contains("Y:"))
                {
                    int ix = t.IndexOf("X:");
                    int iy = t.IndexOf("Y:");
                    string sx = t.Substring(ix + 2).Split(new char[] { ',', ' ', '\n', '\r' })[0];
                    string sy = t.Substring(iy + 2).Split(new char[] { ',', ' ', '\n', '\r' })[0];
                    if (int.TryParse(sx, out int x) && int.TryParse(sy, out int y))
                    {
                        pos = new Position(x, y);
                        return true;
                    }
                }

                // 尝试 "X,Y" 形式
                var nums = t.Split(new char[] { ',', ';', '(', ')', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => s.Trim())
                            .Where(s => s.Length > 0)
                            .ToArray();
                // 找两个连续的数字
                for (int i = 0; i < nums.Length - 1; i++)
                {
                    if (int.TryParse(nums[i], out int a) && int.TryParse(nums[i + 1], out int b))
                    {
                        pos = new Position(a, b);
                        return true;
                    }
                }
            }
            catch
            {
                // 忽略解析错误，返回 false
            }
            return false;
        }

        // 检查碰撞，若碰撞则设置 collisionReason 并返回 true
        private bool CheckCollisions()
        {
            // 边界碰撞（碰到 #）
            if (snake.Head.X <= 0 || snake.Head.X >= BoundarySize ||
                snake.Head.Y <= 0 || snake.Head.Y >= BoundarySize)
            {
                collisionReason = "撞到边界";
                return true;
            }

            // 自身碰撞
            if (snake.Body.Skip(1).Any(p => p.X == snake.Head.X && p.Y == snake.Head.Y))
            {
                collisionReason = "撞到自己";
                return true;
            }

            return false;
        }

        // 每 3 秒与 AI 互动一次：发送当前状态并在 aiLine 显示 AI 的鼓励/建议
        private async Task AIInteractionLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    string stateSummary = $"Score:{score}, Time:{(int)(DateTime.Now - startTime).TotalSeconds}s, Head:({snake.Head.X},{snake.Head.Y}), Food:({food.X},{food.Y}), Len:{snake.Body.Count}";
                    string aiReply = await aiService.GetInteractionAsync(stateSummary);

                    // 在固定行显示 AI 回复（清空该行再写）
                    lock (_consoleLock)
                    {
                        Console.SetCursorPosition(0, aiLine);
                        Console.Write(new string(' ', Console.WindowWidth));
                        Console.SetCursorPosition(0, aiLine);
                        // 限制长度以免挤占太多行
                        string toShow = aiReply;
                        if (toShow.Length > Console.WindowWidth - 1) toShow = toShow.Substring(0, Console.WindowWidth - 4) + "...";
                        Console.Write($"AI: {toShow}");
                    }
                }
                catch
                {
                    // 忽略 AI 调用异常：不影响游戏进行
                }

                try
                {
                    await Task.Delay(3000, token); // 每 3 秒一次
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        // 游戏结束后调用 AI 生成总结并在屏幕显示
        private async Task DisplayGameOverAndSummary()
        {
            // 调用 AI 生成复盘总结（包含亮点与改进建议）
            string prompt = $"玩家本局得分 {score}，存活 {survivalTime} 秒，最大蛇身长度 {maxSnakeLength}，碰撞原因：{collisionReason}。请生成：1) 一句简短的本局总结（语气建设性且具体），2) 一条长期训练建议（面向多局表现），返回纯文本。";

            string aiSummary;
            try
            {
                aiSummary = await aiService.GenerateSummaryAsync(prompt);
            }
            catch (Exception ex)
            {
                aiSummary = $"AI 复盘失败：{ex.Message}";
            }

            // 清屏并显示结算信息与复盘
            lock (_consoleLock)
            {
                Console.Clear();
                Console.SetCursorPosition(0, 0);
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

    // 简单的蛇类：维护身体队列、方向、移动与增长
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

        // 移动：将头部插入并移除尾部（若要求增长，由外部调用 Grow()）
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

        // 增长：在尾部再追加当前尾部位置（下一帧移动时尾部不会被移除，从而实现增长）
        public void Grow()
        {
            Body.Add(Body.Last());
        }
    }

    // 坐标结构体
    public struct Position
    {
        public int X;
        public int Y;
        public Position(int x, int y) { X = x; Y = y; }
    }

    // 方向枚举
    public enum Direction { Up, Down, Left, Right }

    // AI 服务：封装对外部 LLM 调用（包含提示、食物生成、互动与复盘）
    public class AIService
    {
        // 请在生产环境中把 apiKey 放在安全位置（环境变量 / 配置文件）
        private readonly string apiKey = "6b11364e-8591-40bd-b257-7b5c0e0b8653";
        private readonly string endpoint = "https://ark.cn-beijing.volces.com/api/v3/chat/completions";
        private readonly HttpClient client;

        public AIService()
        {
            client = new HttpClient();
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                // 每次请求仍然会设置 Authorization header（也可以在 DefaultRequestHeaders 设一次）
                // client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            }
        }

        // 获取即时策略提示（可并行调用，不等待返回）
        public async Task<string> GetHintAsync(string gameState)
        {
            // 这是一个非阻塞的示例请求，返回原始 LLM 响应文本（无需解析）
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
            string payload = System.Text.Json.JsonSerializer.Serialize(body);
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            content.Headers.Remove("Content-Type");
            content.Headers.Add("Content-Type", "application/json");
            var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            req.Headers.Add("Authorization", $"Bearer {apiKey}");

            try
            {
                var resp = await client.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                var text = await resp.Content.ReadAsStringAsync();
                // 尽量从返回中提取有意义的片段（很依赖具体API的返回结构）
                // 这里我们直接返回原始文本（调用方可以进一步解析）
                return text;
            }
            catch (Exception ex)
            {
                return $"[AI 提示失败：{ex.Message}]";
            }
        }

        // AI 生成食物位置（期望返回格式 X:5,Y:3 或 5,3）
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
            string payload = System.Text.Json.JsonSerializer.Serialize(body);
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            req.Headers.Add("Authorization", $"Bearer {apiKey}");
            try
            {
                var resp = await client.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                var text = await resp.Content.ReadAsStringAsync();
                // 直接返回原始文本给上层解析器（TryParsePosition）
                return text;
            }
            catch (Exception ex)
            {
                return $"食物位置生成失败: {ex.Message}";
            }
        }

        // 每 3s 的互动（鼓励/短评）：传入当前状态，期待简短文本返回
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
            string payload = System.Text.Json.JsonSerializer.Serialize(body);
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            req.Headers.Add("Authorization", $"Bearer {apiKey}");
            try
            {
                var resp = await client.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                var text = await resp.Content.ReadAsStringAsync();
                // 返回原始结果；上层会截短显示
                return ExtractTextFromApiResponse(text);
            }
            catch (Exception ex)
            {
                return $"[互动失败：{ex.Message}]";
            }
        }

        // 游戏结束时生成复盘总结（期待简短总结 + 一条改进建议）
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
            string payload = System.Text.Json.JsonSerializer.Serialize(body);
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            req.Headers.Add("Authorization", $"Bearer {apiKey}");
            try
            {
                var resp = await client.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                var text = await resp.Content.ReadAsStringAsync();
                // 尝试从响应中提取纯文本
                return ExtractTextFromApiResponse(text);
            }
            catch (Exception ex)
            {
                throw new Exception($"生成复盘失败：{ex.Message}");
            }
        }

        // 简单尝试从 API 返回中抽取可读文本（不同的 LLM API 返回格式不同）
        private string ExtractTextFromApiResponse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            // 若返回 JSON 且包含 "choices" / "content" 等字段，需要解析 - 这里尝试粗略寻找常见字段
            try
            {
                // 尝试解析为 JSON 并查找 "text" 或 "content"
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                // 深度查找字符串节点（取第一个较长的文本）
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
                // 不是 JSON 或解析失败 -> 直接继续返回原始
            }

            // 作为最后手段：移除多余的控制字符并返回
            var cleaned = raw.Replace("\r", " ").Replace("\n", " ").Trim();
            if (cleaned.Length > 2000) cleaned = cleaned.Substring(0, 2000) + "...";
            return cleaned;
        }
    }
}
