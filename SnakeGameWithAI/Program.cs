using System;
using System.Threading;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace SnakeGame
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // 显示开始界面并获取用户选择
            GameMode mode = ShowStartMenu();
            
            Game game = new Game(mode);
            await game.Start();
        }

        // 开始界面菜单
        private static GameMode ShowStartMenu()
        {
            Console.CursorVisible = true;
            Console.Clear();
            Console.WriteLine("===== 贪吃蛇游戏 =====");
            Console.WriteLine("1. 本地关卡 (随机生成食物)");
            Console.WriteLine("2. AI生成关卡 (AI决定食物位置)");
            Console.WriteLine("\n请选择模式 (1/2):");

            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.KeyChar == '1')
                    return GameMode.Local;
                if (key.KeyChar == '2')
                    return GameMode.AI;
            }
        }
    }

    // 游戏模式枚举
    public enum GameMode
    {
        Local,
        AI
    }

    public class Game
    {
        private const int BoundarySize = 20; // 边界大小
        private Snake snake;
        private Position food;
        private AIService aiService;
        private GameMode gameMode;

        public Game(GameMode mode)
        {
            gameMode = mode;
            snake = new Snake(new Position(10, 10));
            aiService = new AIService();
            GenerateFood().Wait(); // 初始化食物
        }

        public async Task Start()
        {
            Console.CursorVisible = false;
            // 启动输入监听
            var inputTask = Task.Run(() => ListenForInput());
            
            while (true)
            {
                Console.Clear();
                DrawBoundary(); // 绘制边界
                DisplayGame();  // 绘制游戏元素
                snake.Move();

                // 检测食物碰撞
                if (snake.Head.X == food.X && snake.Head.Y == food.Y)
                {
                    snake.Grow(); // 吃到食物后蛇增长
                    await GenerateFood(); // 生成新的食物
                }

                CheckCollisions(); // 检查碰撞
                await aiService.GetHintAsync($"Snake at ({snake.Head.X},{snake.Head.Y}), Food at ({food.X},{food.Y})");
                Thread.Sleep(100);
            }
        }

        // 绘制边界
        private void DrawBoundary()
        {
            // 上边界和下边界
            for (int x = 0; x <= BoundarySize; x++)
            {
                Console.SetCursorPosition(x, 0);
                Console.Write("#");
                Console.SetCursorPosition(x, BoundarySize);
                Console.Write("#");
            }

            // 左边界和右边界
            for (int y = 1; y < BoundarySize; y++)
            {
                Console.SetCursorPosition(0, y);
                Console.Write("#");
                Console.SetCursorPosition(BoundarySize, y);
                Console.Write("#");
            }
        }

        // 监听用户输入
        private void ListenForInput()
        {
            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    switch (key.Key)
                    {
                        case ConsoleKey.UpArrow:
                            if (snake.CurrentDirection != Direction.Down)
                                snake.CurrentDirection = Direction.Up;
                            break;
                        case ConsoleKey.DownArrow:
                            if (snake.CurrentDirection != Direction.Up)
                                snake.CurrentDirection = Direction.Down;
                            break;
                        case ConsoleKey.LeftArrow:
                            if (snake.CurrentDirection != Direction.Right)
                                snake.CurrentDirection = Direction.Left;
                            break;
                        case ConsoleKey.RightArrow:
                            if (snake.CurrentDirection != Direction.Left)
                                snake.CurrentDirection = Direction.Right;
                            break;
                        case ConsoleKey.Escape:
                            Environment.Exit(0);
                            break;
                    }
                }
                Thread.Sleep(10);
            }
        }

        private void DisplayGame()
        {
            // 绘制蛇
            foreach (var position in snake.Body)
            {
                Console.SetCursorPosition(position.X, position.Y);
                Console.Write("■");
            }

            // 绘制食物
            Console.SetCursorPosition(food.X, food.Y);
            Console.Write("●");
        }

        // 生成食物（根据模式选择生成方式）
        private async Task GenerateFood()
        {
            if (gameMode == GameMode.AI)
            {
                // AI模式：调用AI生成食物位置
                string aiResponse = await aiService.GetFoodPositionAsync(snake.Body);
                // 解析AI返回的位置（实际项目中需要完善解析逻辑）
                if (TryParsePosition(aiResponse, out Position aiFood) && IsValidPosition(aiFood))
                {
                    food = aiFood;
                    return;
                }
            }

            // 本地模式：随机生成（默认 fallback）
            Random rand = new Random();
            Position newFood;
            do
            {
                // 在边界内部生成（1到BoundarySize-1之间）
                newFood = new Position(
                    rand.Next(1, BoundarySize), 
                    rand.Next(1, BoundarySize)
                );
            } while (!IsValidPosition(newFood));

            food = newFood;
        }

        // 验证位置是否有效（不在蛇身上且在边界内）
        private bool IsValidPosition(Position pos)
        {
            return pos.X > 0 && pos.X < BoundarySize && 
                   pos.Y > 0 && pos.Y < BoundarySize &&
                   !snake.Body.Any(p => p.X == pos.X && p.Y == pos.Y);
        }

        // 简单解析位置字符串（示例实现）
        private bool TryParsePosition(string input, out Position pos)
        {
            pos = new Position(0, 0);
            try
            {
                // 假设AI返回格式类似 "X:5,Y:3"
                var parts = input.Split(',');
                if (parts.Length != 2) return false;
                
                int x = int.Parse(parts[0].Split(':')[1].Trim());
                int y = int.Parse(parts[1].Split(':')[1].Trim());
                pos = new Position(x, y);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void CheckCollisions()
        {
            // 边界碰撞（碰到#边界）
            if (snake.Head.X <= 0 || snake.Head.X >= BoundarySize || 
                snake.Head.Y <= 0 || snake.Head.Y >= BoundarySize)
            {
                GameOver();
            }

            // 自身碰撞
            if (snake.Body.Skip(1).Any(p => p.X == snake.Head.X && p.Y == snake.Head.Y))
            {
                GameOver();
            }
        }

        private void GameOver()
        {
            Console.Clear();
            Console.WriteLine("游戏结束!");
            Console.WriteLine($"最终长度: {snake.Body.Count}");
            Console.WriteLine("按任意键退出...");
            Console.ReadKey();
            Environment.Exit(0);
        }
    }

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
            Body.Add(Body.Last()); // 蛇增长
        }
    }

    public struct Position
    {
        public int X;
        public int Y;

        public Position(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    public enum Direction
    {
        Up,
        Down,
        Left,
        Right
    }

    public class AIService
    {
        private readonly string apiKey = "6b11364e-8591-40bd-b257-7b5c0e0b8653";

        public async Task<string> GetHintAsync(string gameState)
        {
            using HttpClient client = new HttpClient();
            var content = new StringContent(
                $"{{\"model\":\"doubao-seed-1-6-251015\",\"max_completion_tokens\":65535,\"messages\":[{{\"content\":[{{\"image_url\":{{\"url\":\"https://ark-project.tos-cn-beijing.ivolces.com/images/view.jpeg\"}}}},{{\"text\":\"当前游戏状态：{gameState}，请分析贪吃蛇下一步应该向哪个方向移动（上/下/左/右）\"}}],\"role\":\"user\"}}],\"reasoning_effort\":\"medium\"}}",
                System.Text.Encoding.UTF8,
                "application/json"
            );

            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            try
            {
                var response = await client.PostAsync("https://ark.cn-beijing.volces.com/api/v3/chat/completions", content);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                return $"AI提示获取失败: {ex.Message}";
            }
        }

        // 获取AI生成的食物位置
        public async Task<string> GetFoodPositionAsync(List<Position> snakeBody)
        {
            string snakePositions = string.Join(";", snakeBody.Select(p => $"({p.X},{p.Y})"));
            using HttpClient client = new HttpClient();
            var content = new StringContent(
                $"{{\"model\":\"doubao-seed-1-6-251015\",\"max_completion_tokens\":65535,\"messages\":[{{\"content\":[{{\"text\":\"贪吃蛇当前位置：{snakePositions}，请在1到19之间（包含1和19）生成一个食物位置，格式为'X:数字,Y:数字'，不要其他内容\"}}],\"role\":\"user\"}}],\"reasoning_effort\":\"medium\"}}",
                System.Text.Encoding.UTF8,
                "application/json"
            );

            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            try
            {
                var response = await client.PostAsync("https://ark.cn-beijing.volces.com/api/v3/chat/completions", content);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                return $"食物位置生成失败: {ex.Message}";
            }
        }
    }
}
