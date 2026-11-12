# SSY226 | NERO Tabletop Simulation

A Unity simulation of an AgileX NERO arm that follows natural-language pick-and-place instructions. The tabletop scene includes a gripper, two colored cubes, three trays, and an RGB observation camera.

## Quick start

1. In Unity Hub, install **Unity 6.3 LTS (6000.3.25f1)** and open the cloned repository as a project.
2. Open [`Assets/NeroTabletop.unity`](Assets/NeroTabletop.unity), enter **Play** mode, and switch to **Game**.
3. Click **Demonstrate red cube to blue tray** for a local grasp-and-place demo. Unity saves the measured transfer trajectory after the cube settles successfully.

## Run a language-guided pick and place

### 1. Start Ollama

With Ollama installed and running locally, download the vision model:

```powershell
ollama pull qwen3-vl:4b-instruct
```

Unity sends one RGB image and the instruction to `http://127.0.0.1:11434/api/chat`.

### 2. Start the motion planner

Run these commands in PowerShell from the Unity repository root. The integration was tested with Python 3.12.

```powershell
git clone https://github.com/KilianFt/DemoStitching.git ../DemoStitching
git -C ../DemoStitching checkout c66bf2882622d5a9a167c562ae3c0b1ac76464c4
Copy-Item -Path ./DemoStitchingIntegration/* -Destination ../DemoStitching -Recurse -Force
Set-Location ../DemoStitching
py -3.12 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\.venv\Scripts\python.exe -u serve_nero_plan.py
```

If the sibling DemoStitching clone already exists, use it in place of the clone step. The planner is ready when it prints `NERO planner ready` for `http://127.0.0.1:8766/plan`. It loads six bundled demonstrations:

| Transfer | Demonstrations |
| --- | ---: |
| Red cube → white tray | 3 |
| Blue cube → yellow tray | 2 |
| Red cube → blue tray | 1 |

### 3. Send an instruction

In the **NERO VLM** panel, enter **Put the red cube in the blue tray.**

- **Send to VLM** shows the selected targets, image coordinates, and projected 3D points.
- **Locate and execute pick-and-place** runs the full simulation, from localization through grasping, transfer, and placement.

## How it works

1. **Unity** captures a frame from the observation camera.
2. **Qwen3-VL** identifies the requested cube and tray and returns two normalized image points.
3. **Unity** projects those points into 3D in the robot base frame (`base_link`).
4. **DemoStitching** generates the transfer trajectory from the lifted grasp pose to the approach above the tray. Unity's inverse kinematics follows the path.
5. **Unity** aligns the gripper, lowers and releases the cube, then checks whether it settled inside the tray.

See the [tabletop guide](TABLETOP使用说明.md) for the scene, camera, and physics checks. The repository includes the [planner integration and training data](DemoStitchingIntegration) and embeds the [URDF Importer](https://github.com/Unity-Technologies/URDF-Importer) and [ROS TCP Connector](https://github.com/Unity-Technologies/ROS-TCP-Connector). This demo uses local HTTP for both services.

Robot assets come from the [AgileX robot descriptions](https://github.com/agilexrobotics/agx_arm_urdf).

