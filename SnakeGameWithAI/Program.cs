// ==================================================
// Program.cs - 控制台贪吃蛇（带 AI 交互）
//
// 概要：本文件实现了一个控制台贪吃蛇游戏，并在三个时机点与 AI 交互：
//   1) 游戏开始（一次性简短鼓励）
//   2) 吃到食物时（即时短提示/策略）
//   3) 游戏结束时（详细复盘和训练建议）
//
// 组织结构（按分割线划分模块）：
//   - 主程序入口与主菜单
//   - 枚举与基础数据结构（Position、Direction）
//   - GameState：集中保存可变游戏状态
//   - ConsoleRenderer：所有屏幕绘制逻辑
//   - InputHandler：键盘事件监听与事件发布
//   - Game：游戏主循环、逻辑协调器
//   - Snake：蛇的移动与增长逻辑
//   - AIService：与 LLM 的 HTTP 请求与响应提取
//
// 可配置项（在 AIService 内部）：模型名、API Key、endpoint。
// 建议：将真实 API Key 移入环境变量以提高安全性。
// ==================================================

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
        /// <summary>
        /// 程序入口：初始化环境（如控制台编码）、创建 AIService 并循环显示主菜单/启动新局。
        /// 主循环在每局结束（或玩家返回主菜单）后继续显示主菜单。
        /// </summary>
        static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            // 创建单例 AIService 并在多局中复用
            var aiService = new AIService();
            while (true)
            {
                var mode = ShowStartMenu();
                var game = new Game(mode, aiService);
                await game.Start();
                // Start 返回后会回到主菜单（例如玩家按 Esc 或游戏结束）
            }
        }

    /// <summary>
    /// 显示主菜单并等待玩家选择模式。
    /// 主菜单中的 Esc 键将退出整个程序；按 '1' 或 '2' 返回对应的 GameMode。
    /// </summary>
    /// <returns>玩家选择的 GameMode</returns>
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

    // ==================================================
    // 模块：GameState
    // 作用：集中存储并管理游戏运行期间的可变状态（分数、暂停/计时、最后的 AI 文本、碰撞原因等）。
    // 设计要点：把所有跨模块需要读取/写入的运行时数据放在单一对象中，便于重构与测试。
    // ==================================================
    /// <summary>
    /// 存放可变的游戏状态数据（分数、暂停计时、最后 AI 消息等）。
    /// </summary>
    public class GameState
    {
        /// <summary>当前得分</summary>
        public int Score { get; set; }

        /// <summary>当前已存活的秒数（计算值，非实时计时器）</summary>
        public int SurvivalTime { get; set; }

        /// <summary>历史最大蛇身长度（本局内的最大值）</summary>
        public int MaxSnakeLength { get; set; }

        /// <summary>本局开始时间（用于计算有效存活时长）</summary>
        public DateTime StartTime { get; set; }

        /// <summary>是否处于暂停状态</summary>
        public bool Paused { get; private set; } = false;

        /// <summary>暂停开始时间（如果处于暂停则有值）</summary>
        public DateTime? PauseStart { get; private set; } = null;

        /// <summary>累计暂停时间（用于在计算生存时减去暂停时长）</summary>
        public TimeSpan PausedTotal { get; private set; } = TimeSpan.Zero;

        /// <summary>碰撞原因（用于在游戏结束时向玩家展示与告知 AI）</summary>
        public string CollisionReason { get; set; } = "";

        /// <summary>AI 的最后一条持久显示文本（例如开始/吃到食物时的提示）</summary>
        public string LastAiMessage { get; set; } = "";

        /// <summary>
        /// 切换暂停状态。若进入暂停，记录 PauseStart；若退出暂停，累加 PausedTotal 并清除 PauseStart。
        /// </summary>
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

        /// <summary>
        /// 计算有效的存活秒数（当前时间 - StartTime - 累计暂停时长）。
        /// 返回值为向下取整的整秒数。
        /// </summary>
        /// <returns>已排除暂停的存活秒数（int）</returns>
        public int ComputeSurvivalSeconds()
        {
            var effectiveElapsed = DateTime.Now - StartTime - PausedTotal;
            return (int)effectiveElapsed.TotalSeconds;
        }
    }

    // ==================================================
    // 模块：ConsoleRenderer
    // 作用：封装所有控制台绘制逻辑，负责把游戏状态渲染到屏幕上。
    // 说明：所有和 UI 布局、光标设置、清屏及各行显示相关的实现都放在这里，
    //       以便将渲染从 Game 逻辑中解耦，便于后续替换渲染实现（例如改为 GUI）。
    // ==================================================
    /// <summary>
    /// 负责把游戏数据渲染到控制台，包括边界、蛇、食物和信息行。
    /// </summary>
    public class ConsoleRenderer
    {
        /// <summary>
        /// 安全设置光标位置：会裁剪坐标到 Buffer 的范围，避免抛出异常。
        /// </summary>
        /// <param name="x">目标列（0 为左侧）</param>
        /// <param name="y">目标行（0 为顶行）</param>
        private void SafeSetCursor(int x, int y)
        {
            int maxX = Math.Max(0, Console.BufferWidth - 1);
            int maxY = Math.Max(0, Console.BufferHeight - 1);
            int cx = Math.Clamp(x, 0, maxX);
            int cy = Math.Clamp(y, 0, maxY);
            Console.SetCursorPosition(cx, cy);
        }

    /// <summary>
    /// 在指定显示区域画出边界（用 '#' 表示），上下左右包含边框。
    /// </summary>
    /// <param name="displayWidth">逻辑显示宽度（包含边界）</param>
    /// <param name="displayHeight">逻辑显示高度（包含边界）</param>
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

    /// <summary>
    /// 在屏幕上绘制游戏内容：蛇身、食物以及信息行（得分/时间/最大长度）。
    /// </summary>
    /// <param name="body">蛇身坐标列表，首元素为头</param>
    /// <param name="food">当前食物坐标</param>
    /// <param name="displayWidth">逻辑显示宽度</param>
    /// <param name="displayHeight">逻辑显示高度</param>
    /// <param name="infoLine">信息行所在行号</param>
    /// <param name="score">当前分数（显示用）</param>
    /// <param name="survivalTime">当前生存时间（秒，显示用）</param>
    /// <param name="maxSnakeLength">本局最大蛇身长度（显示用）</param>
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

    /// <summary>
    /// 在固定行打印 AI 的持久化文本（如开始提示或吃到食物后的短提示），
    /// 并在暂停时追加暂停提示。
    /// </summary>
    /// <param name="lastAiMessage">最后一条 AI 文本</param>
    /// <param name="aiPersistentLine">持久化文本所在行号</param>
    /// <param name="paused">是否处于暂停状态（如果是，会显示暂停提示）</param>
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

    /// <summary>
    /// 每帧渲染入口：清屏、绘制边界、绘制游戏内容以及打印 AI 行。
    /// </summary>
    /// <remarks>此方法会清空控制台并完整重绘当前帧。</remarks>
    public void RenderFrame(List<Position> body, Position food, int displayWidth, int displayHeight, int infoLine, int aiPersistentLine, string lastAiMessage, int score, int survivalTime, int maxSnakeLength, bool paused)
        {
            Console.Clear();
            DrawBoundary(displayWidth, displayHeight);
            DisplayGame(body, food, displayWidth, displayHeight, infoLine, score, survivalTime, maxSnakeLength);
            PrintPersistentAiLine(lastAiMessage, aiPersistentLine, paused);
        }
    }

    // ==================================================
    // 模块：InputHandler
    // 作用：在后台监听键盘事件并通过委托/事件向外发布（方向、暂停、退出请求）。
    // 说明：将输入监听放到独立线程并通过事件通知上层，避免阻塞游戏主循环。
    // ==================================================
    /// <summary>
    /// 键盘输入处理器：在后台循环读取 Console 键盘事件并通过事件分发给订阅者。
    /// </summary>
    public class InputHandler
    {
        /// <summary>当玩家按方向键时触发，参数为新的方向</summary>
        public event Action<Direction>? DirectionChanged;
        /// <summary>当玩家按暂停键（Space）时触发</summary>
        public event Action? PauseToggled;
        /// <summary>当玩家按退出键（Esc）时触发</summary>
        public event Action? ExitRequested;

        private bool _running = true;

        /// <summary>
        /// 启动后台监听（非阻塞）。该方法会在后台任务中执行 RunLoop。
        /// </summary>
        public void Start()
        {
            Task.Run(() => RunLoop());
        }

        /// <summary>
        /// 后台循环：轮询 Console.KeyAvailable 并根据按键分发事件。
        /// 注意：此方法在单独线程/任务中运行，不应直接从主线程调用以免阻塞。
        /// </summary>
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

        /// <summary>
        /// 停止后台监听循环（安全地退出 RunLoop）。
        /// </summary>
        public void Stop() => _running = false;
    }

    // ==================================================
    // 模块：Game
    // 作用：协调游戏逻辑的核心类，包含游戏主循环、事件绑定、AI 交互触发点。
    // 说明：Game 通过组合 GameState、ConsoleRenderer、InputHandler 和 AIService 来实现
    //       游戏流程的控制与视图更新。
    // ==================================================
    /// <summary>
    /// 游戏主类：管理回合流程、碰撞检测、AI 交互与渲染协调。
    /// </summary>
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

            // 由 Game 组合使用的辅助组件（状态、渲染器、输入处理器）
        private readonly GameState state;
        private readonly ConsoleRenderer renderer;
        private readonly InputHandler inputHandler;
    // 当为 true 时表示玩家在游戏内按下 Esc，要求返回主菜单
    private volatile bool exitToMenuRequested = false;

    /// <summary>
    /// 构造函数：初始化显示尺寸、状态、渲染器、输入处理器并绑定事件。
    /// </summary>
    /// <param name="mode">游戏模式（本地随机或由 AI 生成关卡）</param>
    /// <param name="aiService">注入的 AI 服务实例（用于与 LLM 交互）</param>
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

    /// <summary>
    /// 游戏主循环入口：
    /// - 在循环中每帧渲染并处理移动、吃食物、与 AI 交互等；
    /// - 若玩家按 Esc，则设置退出标志并返回到主菜单（不触发游戏结束复盘）；
    /// - 正常死亡时停止输入监听并请求 AI 生成复盘文本。
    /// </summary>
    /// <returns>Task（异步执行）</returns>
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

    // 注意：输入由 InputHandler 负责，渲染由 ConsoleRenderer 负责，Game 只协调二者

    /// <summary>
    /// 生成食物位置：
    /// - 若为 AI 模式，尝试请求 AI 返回位置字符串并解析；解析失败或非 AI 模式则随机生成合法位置。
    /// </summary>
    /// <returns>Task（异步，用于可能的 AI 请求）</returns>
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

        /// <summary>判断点是否在显示区域内部（不包含边界）</summary>
        private bool IsInsideDisplayArea(Position p)
        {
            return p.X > 0 && p.X < displayWidth && p.Y > 0 && p.Y < displayHeight;
        }

        /// <summary>
        /// 判断位置是否合法：必须在显示区域内且不能与蛇身冲突。
        /// </summary>
        private bool IsValidPosition(Position pos)
        {
            return pos.X > 0 && pos.X < displayWidth && pos.Y > 0 && pos.Y < displayHeight &&
                   !snake.Body.Any(b => b.X == pos.X && b.Y == pos.Y);
        }

    /// <summary>
    /// 尝试从 AI 或其它文本输入中解析出坐标位置。支持格式：
    /// - X:数字,Y:数字
    /// - 数字,数字
    /// - 任意文本中包含连续的两个整数
    /// </summary>
    /// <param name="input">待解析的文本</param>
    /// <param name="pos">解析成功时输出的坐标</param>
    /// <returns>解析成功返回 true，否则 false</returns>
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

    /// <summary>
    /// 检查当前蛇头是否与边界或自身发生碰撞，并设置相应的 CollisionReason。
    /// </summary>
    /// <returns>若发生碰撞则返回 true，否则 false</returns>
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

    // ==================================================
    // 模块：Snake（蛇）
    // 作用：表示蛇的坐标集合并实现移动与增长的规则。
    // 说明：将移动/增长逻辑封装在此类，Game 通过调用 Move/Grow 控制行为。
    // ==================================================
    /// <summary>
    /// 表示蛇实体：维护坐标列表与当前方向，并支持移动与增长操作。
    /// </summary>
    public class Snake
    {
        /// <summary>蛇身坐标列表，首元素为头</summary>
        public List<Position> Body { get; set; }

        /// <summary>蛇头位置（Body 的第一个元素）</summary>
        public Position Head => Body.First();

        /// <summary>当前移动方向</summary>
        public Direction CurrentDirection { get; set; }

        // 当为 true 时，下一次 Move() 将扩展蛇身（即本次移动不删除尾部）
        private bool pendingGrow = false;

        /// <summary>
        /// 构造一条蛇并设置初始出生点与默认朝向（向右）。
        /// </summary>
        /// <param name="spawn">出生坐标</param>
        public Snake(Position spawn)
        {
            Body = new List<Position> { spawn };
            CurrentDirection = Direction.Right;
        }

        /// <summary>
        /// 执行一次移动：在当前方向计算新头并插入到 Body 首位。
        /// 如果此前调用过 Grow()，则本次移动不删除尾部以实现增长；否则删除尾部保持长度不变。
        /// </summary>
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
            // 若之前请求过增长，则本次移动保持尾部以增长长度；否则移除尾部保持长度不变
            if (pendingGrow)
            {
                pendingGrow = false;
            }
            else
            {
                Body.RemoveAt(Body.Count - 1);
            }
        }

        /// <summary>
        /// 请求在下一次 Move() 时增长蛇身（通过在下一次移动时不移除尾部实现）。
        /// </summary>
        public void Grow()
        {
            // 标记下一次移动应当增长
            pendingGrow = true;
        }
    }

    public struct Position { public int X; public int Y; public Position(int x, int y) { X = x; Y = y; } }
    public enum Direction { Up, Down, Left, Right }

    // ==================================================
    // 模块：AIService
    // 作用：封装与 LLM（聊天模型）的所有交互细节，包括请求体构造、HTTP 发送与响应提取。
    // 说明：将模型配置、API key 与请求/提取逻辑集中，便于以后替换模型或修改请求策略。
    // ==================================================
    /// <summary>
    /// 提供与远端 LLM 交互的高层方法：游戏开始提示、吃食物时提示、生成食物位置与游戏复盘。
    /// </summary>
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

    /// <summary>
    /// 在游戏开始时调用：请求 AI 给出一次简短（单句/短语）的鼓励或提示。
    /// </summary>
    /// <param name="stateSummary">当前游戏模式或简短状态摘要（用于上下文）</param>
    /// <returns>AI 返回的单行提示文本（string）</returns>
    public async Task<string> GetInteractionOnceAsync(string stateSummary)
        {
            // 系统提示加强：要求多样性、避免重复、限制长度并提供风格选项
            var body = BuildChatBody(
                ("system", "你是友好的游戏助手。每次回复要保持表达多样性，避免重复之前在同一局或同一会话中使用过的整句或固定短语。可在风格上随机选择：幽默、直率、温和或简洁，但每次仅输出几句中文（不超过30字），内容须是具体的鼓励或可执行的小建议。不要解释、不要额外文本、不要引号。"),
                ("user", stateSummary + " 请只输出几句中文鼓励或实用提示（<=30字），保持与之前不同的措辞或风格，不要解释、不要多余文本、不要引号.")
            );
            return await PostAndExtractSingleLineAsync(body);
        }

        /// <summary>
        /// 在蛇吃到食物时调用：请求 AI 给出简短的鼓励或局面相关小策略。
        /// </summary>
        /// <param name="stateSummary">关于当前局面的简短描述（供 AI 参考）</param>
        /// <returns>AI 的单行提示文本</returns>
        public async Task<string> GetInteractionOnEatAsync(string stateSummary)
        {
            // 吃到食物时的提示也应多样化，并可包含基于当前状态的小策略
            var body = BuildChatBody(
                ("system", "你是友好的游戏助手。吃到食物时要给出简短且多样化的几句中文提示（<=30字），可以是鼓励或基于当前局面的小策略。避免与本局之前的提示重复。不要解释、不要多余文本、不要引号。"),
                ("user", stateSummary + " 请只输出几句中文鼓励或简短策略（<=30字），措辞要与之前不同，不要解释、不要多余文本、不要引号.")
            );
            return await PostAndExtractSingleLineAsync(body);
        }

        /// <summary>
        /// 请求 AI 为当前局面生成一个食物坐标（仅返回坐标文本）。
        /// </summary>
        /// <param name="snakeBody">当前蛇身坐标列表（供 AI 避免冲突）</param>
        /// <returns>AI 返回的坐标文本，需由调用方解析</returns>
        public async Task<string> GetFoodPositionAsync(List<Position> snakeBody)
        {
            string snakePositions = string.Join(";", snakeBody.Select(p => $"({p.X},{p.Y})"));
            var body = BuildChatBody(
                ("system", "你是游戏地图/关卡生成器。只返回坐标，不要说明。"),
                ("user", $"贪吃蛇当前位置：{snakePositions}。请在1到19之间生成一个食物位置，格式 'X:数字,Y:数字' 或 '数字,数字'，只返回位置，不要其他说明。")
            );
            return await PostAndExtractAsync(body);
        }

        /// <summary>
        /// 在游戏结束时请求 AI 根据总结 prompt 输出复盘与训练建议（多句/结构化文本）。
        /// </summary>
        /// <param name="prompt">包含本局关键信息的提示字符串</param>
        /// <returns>AI 生成的复盘文本（string）</returns>
        public async Task<string> GenerateSummaryAsync(string prompt)
        {
            // 要求复盘富有变化且具体：几句本局总结 + 若干可执行训练建议并标注优先级
            var body = BuildChatBody(
                ("system", "你是游戏教练。返回时请遵循：1) 用几句独特且具体的本局总结（避免通用模板和与之前重复的句子）；2) 给出2到3条可执行的长期训练建议，并为每条建议标注优先级（高/中/低）和简短原因。返回纯文本，结构清晰但不要包含多余闲话。"),
                ("user", prompt)
            );
            return await PostAndExtractAsync(body);
        }

        /// <summary>
        /// 根据 role/content 对构建请求的消息体（匿名对象），便于序列化为 JSON。
        /// </summary>
        private object BuildChatBody(params (string role, string content)[] messages)
        {
            var msgs = messages.Select(m => new { role = m.role, content = m.content }).ToArray();
            return new { model = model, messages = msgs };
        }

        /// <summary>
        /// 发送 HTTP POST 请求到模型 endpoint 并返回原始响应字符串。
        /// </summary>
        /// <param name="body">将被序列化为 JSON 的请求体对象</param>
        /// <returns>HTTP 响应的原始字符串</returns>
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

        /// <summary>
        /// 向 API POST 并提取单行短回复：优先返回首条非空句子，同时做合理性校验（中文字符、非哈希等）。
        /// </summary>
        /// <param name="body">请求体对象</param>
        /// <returns>提取后的短文本，若不可用则返回默认鼓励语或错误信息</returns>
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

        /// <summary>
        /// 通用 POST + 提取（适用于位置/复盘等需要多行或复杂文本的场景）。
        /// </summary>
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

        /// <summary>
        /// 从原始响应中尽力提取可读文本：
        /// 1) 优先尝试 choices[0].message.content；
        /// 2) 回退到遍历 JSON 字段并挑选最可能是正文的字符串；
        /// 3) 若不是 JSON 或提取失败，则返回清理后的原始字符串片段。
        /// </summary>
        /// <param name="raw">HTTP 响应字符串</param>
        /// <returns>提取到的正文文本（尽量可读）</returns>
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
