# NERO Tabletop Simulation

Open `Assets/NeroTabletop.unity`. The original `NeroSimulation.unity` scene is retained.

## Included Setup
- A 1.4 m × 1.0 m workbench with a tabletop height of 0.74 m, with static colliders for the tabletop and legs.
- The NERO base is positioned at (-0.45, 0.74, 0), with its root ArticulationBody fixed.
- Two red and blue cubes, each 5 cm wide and weighing 80 g, with gravity, friction, and continuous collision detection enabled.
- Two open trays, one white and one yellow, with separate colliders for the bottom and each of the four walls.
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

Inverse kinematics, automatic grasping, ROS message publishing, and VLM integration have not yet been added. Full reachability of the fixed target positions still needs to be verified after controller integration.

Reset blocks resets only the cubes. Stop and restart Play mode to restore the robot arm to its initial state.

## Verification
An actual five-second Play mode check has been completed: the cubes settle on the tabletop, the robot base remains fixed, and all movable joints hold their target poses.

Verification log: `Verification/physics-check.txt`.

The Unity Test Framework dependency has been added, and the scene-building script has compiled and executed successfully.
