# NERO 桌面仿真

打开 Assets/NeroTabletop.unity。原始 NeroSimulation.unity 保留。

## 已搭建
- 1.4 m × 1.0 m 实验桌，桌面高度 0.74 m；静态桌面与桌腿碰撞体。
- NERO 底座位于 (-0.45, 0.74, 0)，根 ArticulationBody 固定。
- 红、蓝两块边长 5 cm 的方块，每块 80 g，具有重力、摩擦和连续碰撞检测。
- 白色、黄色两个开口托盘；底面和四面墙分别建立碰撞体。
- Main Camera 用于整体预览。
- Observation Camera - RGB 用于俯视观察，输出到 Assets/ObservationRGB.renderTexture，分辨率 640×480。
- 灯光、地板和材质。
- 运行时左上角 Reset blocks 按钮重置两块物体的位置、旋转和速度。

## 操作
1. 打开场景，点击 Play 查看仿真。
2. 在 Hierarchy 展开 Workspace，选择 Table、Movable Blocks 或托盘调整位置。
3. 在 Project 选择 ObservationRGB.renderTexture 查看虚拟相机预览。
4. 菜单 NERO > Verify Physics (5 seconds) 可运行基础物理检查；运行结束自动停止。
5. Verification/overview.png 与 observation.png 是相机输出样例。

## 当前范围
机械臂使用初始关节姿态保持，原导入器键盘 Controller 暂停使用。
NeroHoldPose 在开始运行时设置初始目标；转动关节采用 Target 驱动，便于先完成环境搭建。
为了避免导入的凸碰撞体在关节处互相挤压，机器人内部碰撞暂时关闭；与桌面、物块的碰撞仍然开启。
这还不是经过硬件标定的动力学模型。后续接入控制器时需调试驱动参数，并恢复、验证所需的自碰撞检测。
尚未加入逆运动学、自动抓取、ROS 消息发布或 VLM。固定点位的完整可达性仍需控制接入后验证。
Reset blocks 仅重置方块；机械臂可通过停止再运行恢复初始状态。

## 验证
已完成实际 Play 模式 5 秒检查：方块落在桌面并停止、机器人底座固定、所有可动关节保持目标姿态。
验证记录：Verification/physics-check.txt。
已补齐 Unity Test Framework 依赖，场景构建脚本已成功编译执行。
