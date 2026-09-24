"""Kokoro synthesis server started by Herald.

Loads the model once, then serves one JSON line per connection:
{"text": ..., "voice": ..., "speed": ..., "lang": ..., "out": ...}
"""
import argparse
import json
import socket
import sys

import soundfile as sf
from kokoro_onnx import Kokoro

parser = argparse.ArgumentParser()
parser.add_argument("--model", required=True)
parser.add_argument("--voices", required=True)
parser.add_argument("--port", type=int, required=True)
args = parser.parse_args()

print("Loading Kokoro model...", file=sys.stderr, flush=True)
kokoro = Kokoro(args.model, args.voices)

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
            samples, sample_rate = kokoro.create(
                text,
                voice=request.get("voice", "am_michael"),
                speed=float(request.get("speed", 1.0)),
                lang=request.get("lang", "en-us"),
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
