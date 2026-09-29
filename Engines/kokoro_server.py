"""Kokoro synthesis server started by Herald.

Loads the model once, then serves one JSON line per connection:
{"text": ..., "voice": ..., "speed": ..., "lang": ..., "out": ...}
"""
import argparse
import json
import socket
import sys

import numpy as np
import soundfile as sf
from kokoro_onnx import Kokoro

# Above this Kokoro starts dropping short words: at 1.9 "The build finished" came out as
# "Bill finished", and "Do you want" as "You want". Faster speeds are reached by speeding
# up the finished audio instead, which keeps every word.
MAX_MODEL_SPEED = 1.2

parser = argparse.ArgumentParser()
parser.add_argument("--model", required=True)
parser.add_argument("--voices", required=True)
parser.add_argument("--port", type=int, required=True)
args = parser.parse_args()

print("Loading Kokoro model...", file=sys.stderr, flush=True)
kokoro = Kokoro(args.model, args.voices)


def speed_up(samples, rate, sample_rate):
    """Plays speech `rate` times faster without raising its pitch (WSOLA): overlapping
    slices are taken further apart than they are laid down, each nudged to where it
    lines up best with the previous one so the joins don't crackle."""
    window = int(0.03 * sample_rate)
    hop_out = window // 2
    hop_in = hop_out * rate
    tolerance = int(0.008 * sample_rate)
    fade = np.hanning(window).astype(np.float32)

    out_length = int(len(samples) / rate)
    source = np.pad(samples.astype(np.float32), (tolerance, window + tolerance + int(hop_in) + 1))
    out = np.zeros(out_length + window, np.float32)
    weight = np.zeros_like(out)

    out_pos, in_pos, previous = 0, 0.0, None
    while out_pos < out_length:
        start = int(in_pos) + tolerance
        if previous is not None:
            natural_next = source[previous + hop_out:previous + hop_out + window]
            nearby = source[start - tolerance:start + tolerance + window]
            start += int(np.argmax(np.correlate(nearby, natural_next, "valid"))) - tolerance
        out[out_pos:out_pos + window] += source[start:start + window] * fade
        weight[out_pos:out_pos + window] += fade
        previous = start
        out_pos += hop_out
        in_pos += hop_in

    weight[weight < 1e-3] = 1
    return (out / weight)[:out_length]


def create(text, voice, speed, lang):
    model_speed = min(speed, MAX_MODEL_SPEED)
    samples, sample_rate = kokoro.create(text, voice=voice, speed=model_speed, lang=lang)
    if speed > model_speed:
        samples = speed_up(samples, speed / model_speed, sample_rate)
    return samples, sample_rate


server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
server.bind(("127.0.0.1", args.port))
server.listen(5)
print("Listening on 127.0.0.1:{}".format(args.port), file=sys.stderr, flush=True)

while True:
    conn, _ = server.accept()
    try:
        data = b""
        while not data.endswith(b"\n"):
            chunk = conn.recv(4096)
            if not chunk:
                break
            data += chunk

        request = json.loads(data.decode("utf-8"))
        text = request.get("text", "")
        out_path = request.get("out")

        if text.strip() and out_path:
            samples, sample_rate = create(
                text,
                request.get("voice", "am_michael"),
                float(request.get("speed", 1.0)),
                request.get("lang", "en-us"),
            )
            sf.write(out_path, samples, sample_rate)
            conn.sendall(b'{"status":"ok"}\n')
        else:
            conn.sendall(b'{"status":"empty"}\n')
    except Exception as e:
        try:
            conn.sendall(json.dumps({"status": "error", "message": str(e)}).encode("utf-8") + b"\n")
        except Exception:
            pass
    finally:
        conn.close()
