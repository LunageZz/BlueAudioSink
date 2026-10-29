"""Measure internal near-zero runs in a standard WAV; this is not a noise verdict.

Requires numpy and scipy. Genuine musical silence must be compared to the source.
"""
import json
import sys
from pathlib import Path

import numpy as np
from scipy.io import wavfile

path = Path(sys.argv[1])
rate, data = wavfile.read(path)
data = data.astype(np.float64) if data.dtype.kind == "f" else data.astype(np.float64) / max(abs(np.iinfo(data.dtype).min), np.iinfo(data.dtype).max)
if data.ndim == 1:
    data = data[:, None]
zero = np.all(np.abs(data) < 1e-7, axis=1)
edges = np.diff(np.r_[False, zero, False].astype(np.int8))
starts, ends = np.flatnonzero(edges == 1), np.flatnonzero(edges == -1)
internal = (starts > 0) & (ends < len(data))
starts, lengths = starts[internal], (ends - starts)[internal]
result = {
    "path": str(path), "rate": rate, "channels": data.shape[1],
    "frames": len(data), "seconds": len(data) / rate,
    "rms": float(np.sqrt(np.mean(data ** 2))),
    "peak": float(np.max(np.abs(data))),
    "maxInternalZeroMs": float(max(lengths, default=0) * 1000 / rate),
    "gapsAtLeast0_5ms": [
        {"startSeconds": float(start / rate), "durationMs": float(length * 1000 / rate)}
        for start, length in zip(starts, lengths) if length >= rate * 0.0005
    ],
}
print(json.dumps(result, ensure_ascii=False, indent=2))
