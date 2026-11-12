# SSY226_SIMULATION

The tabletop scene supports VLM localization and simulated pick-and-place. See the [tabletop instructions](TABLETOP使用说明.md).

## Install and open

1. Run the signed UnityHubSetup.exe provided beside this project.
2. Sign in to your Unity account and activate a license appropriate for your use.
3. In Hub > Installs, install Unity 6.3 LTS, version 6000.3.25f1.
4. The base Windows editor is enough for the first simulation. Leave Android, iOS,
   WebGL, and extra platform modules unselected. Visual Studio 2022 is already installed.
5. In Hub > Projects > Add, select this NeroUnity folder, then open it.
6. Wait for asset imports. Open Window > Package Manager and confirm URDF Importer,
   ROS TCP Connector, and Editor Coroutines appear. All three are embedded locally.
7. Check the Console for compilation errors. Compatibility with Unity 6.3 is pending
   this actual editor check; older robotics package documentation is not proof of compatibility.
8. In the Project panel, find Assets/Robots/nero_with_gripper.urdf. Right-click it and
   choose Import Robot from Selected URDF file. Import as an articulation robot.
9. Check the imported robot's axes, joint limits, colliders, and gripper mimic behaviour
   before trying contact-based grasping. The URDF is a model, not a complete controller.

## Included

- URDF Importer v0.5.2, embedded package.
- ROS TCP Connector v0.7.1, embedded package.
- Editor Coroutines 1.0.0, embedded dependency.
- Official AgileX NERO and AGX gripper meshes and model descriptions.
- Flattened gripper URDF: only nested include elements were expanded; robot parameters
  are unchanged. All 24 visual/collision mesh references were checked on disk.
- SETUP-STATUS.json records the model commit and package revisions.

## ROS 2 and Python backend

The sibling NeroROS2/src/ROS-TCP-Endpoint folder contains the official main-ros2
endpoint source. It still needs to be built inside an installed ROS 2 environment.
For automatic pick-and-place, clone [DemoStitching](https://github.com/KilianFt/DemoStitching)
beside this Unity project and check out revision `c66bf2882622d5a9a167c562ae3c0b1ac76464c4`.
Copy the contents of `DemoStitchingIntegration/` into the DemoStitching clone.
Install the upstream Python dependencies (the local integration was tested with Python 3.12),
then run `python serve_nero_plan.py` in that environment from the DemoStitching folder.
The planner fits the six bundled demonstrations and serves Unity at
`http://127.0.0.1:8766/plan`. Start local Ollama as described in the tabletop
instructions, then use **Locate and execute pick-and-place** in the Game view.

For ROS 2 communication, configure Robotics > ROS Settings to use ROS 2 and point
at the endpoint machine's address. Unity's Connector alone does not install ROS 2.
Use a fixed simulation timestep and explicit conversions between Unity and ROS coordinates.

## Sources

https://github.com/Unity-Technologies/URDF-Importer
https://github.com/Unity-Technologies/ROS-TCP-Connector
https://github.com/Unity-Technologies/ROS-TCP-Endpoint/tree/main-ros2
https://github.com/agilexrobotics/agx_arm_urdf
https://github.com/KilianFt/DemoStitching

## Collision importer repair

The embedded URDF importer was patched to save VHACD hulls as
`<mesh>_vhacd_hull_<index>.asset`. Upstream used `<mesh>_<index>.asset`,
which conflicts with generated STL submesh names and can invalidate source
meshes during import (NullReferenceException at VHACD.cs:143).
After this repair, repeat the URDF import with Overwrite Existing Prefabs
enabled to regenerate the previously affected STL submeshes and prefabs.
Actual re-import verification remains pending in the open editor.

