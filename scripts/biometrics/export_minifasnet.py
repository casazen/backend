#!/usr/bin/env python3
"""Export MiniFASNetV2 of Silent-Face-Anti-Spoofing from its PyTorch release to ONNX (E22/F01/US01/T01, ADR-013-R8).

THE RECIPE ONLY. Nobody runs it automatically: it is not part of the build, of the tests or of the CI, and no model file is exported,
downloaded or committed without the permission of the product owner (decision O7, docs/runbooks/biometric-models.md). The exported file is
then measured and hashed by scripts/biometrics/fetch-models.mjs (--only minifasnet), which prints the Railway variable to set.

What the application expects of the file (Casazen.Infrastructure/Services/AntiSpoofingService.cs, appsettings.json Biometrics:AntiSpoofing):
  - input  1x3x80x80, float, the face cut at 2.7 times its box, channels in BGR order, values 0..255 (the release does not normalise);
  - output 1x3: class 0 = print attack, class 1 = real face, class 2 = screen attack (logits are fine: the application applies the softmax).

Exact versions the recipe is written against (a comment, not a lock: write the ones you really used in the notes of the manifest entry):
    pip install torch==2.2.2 onnx==1.16.0 onnxruntime==1.17.1 numpy==1.26.4

Steps, once the product owner says yes:
    git clone https://github.com/minivision-ai/Silent-Face-Anti-Spoofing
    git -C Silent-Face-Anti-Spoofing checkout <commit>     # the commit goes into the manifest: source.revision of "minifasnet"
    python scripts/biometrics/export_minifasnet.py --repo Silent-Face-Anti-Spoofing \\
        --output Casazen.Web/MlModels/biometrics/2.7_80x80_MiniFASNetV2.onnx
    node scripts/biometrics/fetch-models.mjs --only minifasnet --revision minifasnet=<commit> --write-manifest

The weights (resources/anti_spoof_models/2.7_80x80_MiniFASNetV2.pth) and the code are Apache-2.0. The training data of the authors is not
published: the DPIA gate of ADR-013 keeps this model out of production until it is confirmed.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

INPUT_SHAPE = (1, 3, 80, 80)
OUTPUT_SHAPE = (1, 3)
REAL_CLASS = 1
DEFAULT_WEIGHTS = "resources/anti_spoof_models/2.7_80x80_MiniFASNetV2.pth"


def kernel_size(height: int, width: int) -> tuple[int, int]:
    """The kernel of the last convolution, as the release computes it from the size of the input (src/utility.py: get_kernel)."""
    return ((height + 15) // 16, (width + 15) // 16)


def load_model(repo: Path, weights: Path):
    """MiniFASNetV2 of the release with its weights loaded, in evaluation mode."""
    import torch

    sys.path.insert(0, str(repo))
    from src.model_lib.MiniFASNet import MiniFASNetV2  # the code of the release, not ours

    model = MiniFASNetV2(embedding_size=128, conv6_kernel=kernel_size(80, 80), drop_p=0.2, num_classes=3, img_channel=3)
    state = torch.load(weights, map_location="cpu")
    # The release saved the weights of a DataParallel model: its keys start with "module.", the model of this script has none.
    state = {key[len("module."):] if key.startswith("module.") else key: value for key, value in state.items()}
    model.load_state_dict(state)
    model.eval()
    return model


def export(model, output: Path) -> None:
    import torch

    output.parent.mkdir(parents=True, exist_ok=True)
    torch.onnx.export(
        model,
        torch.zeros(INPUT_SHAPE),
        str(output),
        input_names=["input"],
        output_names=["output"],
        opset_version=13,
        do_constant_folding=True,
    )


def check(model, output: Path) -> None:
    """The file loads, takes 1x3x80x80, answers 1x3 and agrees with the PyTorch model on one input. Skipped, with a notice, without onnxruntime."""
    import torch

    try:
        import numpy as np
        import onnxruntime
    except ImportError:
        print("onnxruntime or numpy is not installed: the exported file was not checked", file=sys.stderr)
        return

    sample = torch.rand(INPUT_SHAPE) * 255.0
    with torch.no_grad():
        expected = model(sample).numpy()

    session = onnxruntime.InferenceSession(str(output), providers=["CPUExecutionProvider"])
    (answer,) = session.run(None, {session.get_inputs()[0].name: sample.numpy()})
    if tuple(answer.shape) != OUTPUT_SHAPE:
        raise SystemExit(f"the exported model answers {tuple(answer.shape)}, expected {OUTPUT_SHAPE}")
    if not np.allclose(answer, expected, atol=1e-3):
        raise SystemExit("the exported model does not agree with the PyTorch model")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("--repo", type=Path, required=True, help="folder of the Silent-Face-Anti-Spoofing clone, at the commit you pin")
    parser.add_argument("--weights", type=Path, help=f"the .pth file (default: <repo>/{DEFAULT_WEIGHTS})")
    parser.add_argument("--output", type=Path, required=True, help="the .onnx file to write")
    arguments = parser.parse_args()

    weights = arguments.weights or arguments.repo / DEFAULT_WEIGHTS
    if not weights.is_file():
        raise SystemExit(f"weights not found: {weights}")

    model = load_model(arguments.repo, weights)
    export(model, arguments.output)
    check(model, arguments.output)
    print(f"exported {arguments.output} (input {INPUT_SHAPE}, output {OUTPUT_SHAPE}, class {REAL_CLASS} is the real face)")
    print("next: node scripts/biometrics/fetch-models.mjs --only minifasnet --revision minifasnet=<commit> --write-manifest")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
