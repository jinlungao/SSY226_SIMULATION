"""Serve a local DemoStitching planner for the NERO Unity simulation."""
import json
from copy import deepcopy
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

import numpy as np

from configs import StitchConfig
from src.stitching.ds_stitching import construct_stitched_ds
from src.stitching.shared_precompute import compute_lpvds_and_graph
from src.util.load_tools import get_demonstration_set

DT = 0.01
PORT = 8766


def read_point(request, key):
    value = request.get(key)
    if not isinstance(value, dict) or any(type(value.get(axis)) not in (float, int) for axis in "xyz"):
        raise ValueError(f"{key} must contain numeric x, y, and z")
    point = np.array([value[axis] for axis in "xyz"], dtype=float)
    if not np.isfinite(point).all():
        raise ValueError(f"{key} must be finite")
    return point


def fit_demonstrations():
    dataset_root = Path(__file__).resolve().parent / "dataset/stitching"
    red_dataset = dataset_root / "nero_unity_transfer"
    datasets = (
        (red_dataset, "red-to-white"),
        (dataset_root / "nero_unity_blue_yellow", "blue-to-yellow"),
        (dataset_root / "nero_unity_red_blue", "red-to-blue"),
    )
    demonstrations = []
    for dataset, label in datasets:
        if not any(dataset.glob("demonstration_*/trajectory_*.json")):
            raise FileNotFoundError(f"No recorded demonstrations found in {dataset}")
        group = get_demonstration_set(str(dataset))
        demonstrations.extend(group)
        print(f"Loaded {sum(len(demo.trajectories) for demo in group)} {label} trajectories.", flush=True)
    config = StitchConfig(dataset_path=str(red_dataset), ds_method="sp_recompute_ds",
                          reverse_gaussians=False, save_fig=False)
    np.random.seed(config.seed)
    return config, compute_lpvds_and_graph(demonstrations, config)


def serve():
    config, fitted = fit_demonstrations()

    class Handler(BaseHTTPRequestHandler):
        def reply(self, status, payload):
            content = json.dumps(payload, allow_nan=False).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(content)))
            self.end_headers()
            self.wfile.write(content)

        def do_POST(self):
            if self.path != "/plan":
                self.reply(404, {"error": "Use POST /plan"})
                return
            try:
                size = int(self.headers.get("Content-Length", "0"))
                if not 0 < size <= 4096:
                    raise ValueError("Invalid request size")
                request = json.loads(self.rfile.read(size))
                if not isinstance(request, dict):
                    raise ValueError("Expected a JSON object")
                start, goal = (read_point(request, key) for key in ("start", "goal"))
            except (ValueError, OverflowError) as error:
                self.reply(400, {"error": str(error)})
                return
            try:
                ds, _, _ = construct_stitched_ds(
                    config, deepcopy(fitted["gg"]), fitted["norm_demo_set"],
                    fitted["ds_set"], fitted["reversed_ds_set"], start, goal)
                if ds is None:
                    raise ValueError("No supported path for this start and goal")
                points = np.asarray(ds.sim(start[None, :], dt=DT)[0], dtype=float)
                if (points.ndim != 2 or points.shape[1] != 3 or not 2 <= len(points) <= 5000
                        or not np.isfinite(points).all() or np.linalg.norm(points[0] - start) > .005
                        or np.linalg.norm(points[-1] - goal) > .011):
                    raise ValueError("The generated trajectory did not converge to the requested goal")
            except Exception as error:
                self.reply(422, {"error": str(error)})
                return
            self.reply(200, {"dt": DT, "points": [dict(zip("xyz", row)) for row in points.tolist()]})

    with HTTPServer(("127.0.0.1", PORT), Handler) as server:
        print(f"NERO planner ready on http://127.0.0.1:{PORT}/plan", flush=True)
        server.serve_forever()


if __name__ == "__main__":
    serve()
