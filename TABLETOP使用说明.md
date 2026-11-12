# NERO Tabletop Simulation

Open `Assets/NeroTabletop.unity`. The original `NeroSimulation.unity` scene is retained.

## Included Setup
- A 1.4 m × 1.0 m workbench with a tabletop height of 0.74 m, with static colliders for the tabletop and legs.
- The NERO base is positioned at (-0.45, 0.74, 0), with its root ArticulationBody fixed.
- Two red and blue cubes, each 5 cm wide and weighing 80 g, with gravity, friction, and continuous collision detection enabled.
- Three open trays (white, yellow, and blue) with separate floor and wall colliders.
- Main Camera provides an overview of the scene.
- Observation Camera - RGB provides a top-down view and renders to `Assets/ObservationRGB.renderTexture` at 640 × 480 resolution.
- Lighting, a floor, and materials.
- A Reset blocks button in the upper-left corner during Play mode resets the position, rotation, and velocity of both cubes.

## Instructions
1. Open the scene and click Play to view the simulation.
2. Expand Workspace in the Hierarchy, then select Table, Movable Blocks, or a tray to adjust its position.
3. Select `ObservationRGB.renderTexture` in the Project window to preview the virtual camera output.
4. Select NERO > Verify Physics (5 seconds) to run a basic physics check. Play mode stops automatically when the check finishes.
5. `Verification/overview.png` and `Verification/observation.png` contain sample camera outputs.

## Current Scope
The robot arm holds its initial joint configuration. The keyboard Controller provided by the original importer is currently disabled.

NeroHoldPose sets the initial targets when Play mode starts. Revolute joints use Target drives to support the initial environment setup.

Robot self-collision is currently disabled to prevent the imported convex colliders from pushing against each other at the joints. Collisions with the table and blocks remain enabled.

This dynamics model has not been calibrated against physical hardware. When integrating a controller, tune the drive parameters and restore and validate the required self-collision detection.

Inverse kinematics and automatic grasping are available in the tabletop scene. The VLM and transfer planner connect over local HTTP.

Reset blocks resets only the cubes. Stop and restart Play mode to restore the robot arm to its initial state.

## VLM interface

1. Start local Ollama on port `11434` with the `qwen3-vl:4b-instruct` model available. Run `ollama pull qwen3-vl:4b-instruct` if the model has not been downloaded.
2. Open `NeroTabletop.unity`, press Play, and open the Game tab.
3. Type an instruction in the **NERO VLM** panel and click **Send to VLM**. For example, try “Put the red cube in the white tray.”
4. The panel shows the IDs and normalized image coordinates alongside the exact RGB frame sent to Ollama. Green marks the pick point and cyan marks the place point. The first request can take longer while the model loads.

`NeroVlmClient` is attached to **Observation Camera - RGB**. It sends one request to `POST http://127.0.0.1:11434/api/chat` with the captured PNG, scene catalog, and command. Other Unity scripts can call `await LocateAsync(command)` on the component from the main thread. The result has `pick` and `place` fields. Each contains an object ID and a two-element `point_2d` array in the range 0–1000, measured from the image's top-left corner, or is null for a missing target.

`NeroPointProjector` is attached to the same camera and references **NERO Arm → world → base_link**. It projects each returned image point using the camera and base transforms saved when the photo was captured. The ray intersects the cube top at world Y = 0.790 m for pick and the tray floor at world Y = 0.752 m for place. Adjust the component's surface heights if the workspace geometry changes. These are estimated surface points in Unity axes.

The **Game** panel shows the estimated `base_link` coordinates in meters. In **Scene**, turn on **Gizmos** to see the green and cyan 3D markers during Play mode. Code can read the nullable `Point3D` property on either target for `WorldPosition` and `BasePosition`. The property is empty when projection is unavailable.

## Verification
An actual five-second Play mode check has been completed: the cubes settle on the tabletop, the robot base remains fixed, and all movable joints hold their target poses.

Verification log: `Verification/physics-check.txt`.

The Unity Test Framework dependency has been added, and the scene-building script has compiled and executed successfully.
