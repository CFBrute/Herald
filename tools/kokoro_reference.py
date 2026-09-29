"""Records what the Python Kokoro pipeline (kokoro-onnx 0.6.1) makes of a set of texts, so
Herald's C# port can be checked against it: the phonemes and batches exactly, the audio
closely. Run with the Python environment that has kokoro-onnx installed:

    python kokoro_reference.py <model.onnx> <voices.bin> <out dir>

Writes <out dir>/kokoro-reference.json and one WAV per audio case.
"""
import json
import sys
from pathlib import Path

import numpy as np
import soundfile as sf
from kokoro_onnx import Kokoro
from kokoro_onnx.chunker import pause_after

model, voices, out = sys.argv[1], sys.argv[2], Path(sys.argv[3])
out.mkdir(parents=True, exist_ok=True)
kokoro = Kokoro(model, voices)

LONG = " ".join(
    f"This is sentence number {i}, and it keeps going for a while; long enough, surely, to fill a batch."
    for i in range(1, 16))

PHONEME_CASES = [
    ("en-us", "Hello world"),
    ("en-us", "The build finished, and all the tests passed."),
    ("en-us", "Do you want me to commit this? Yes, you can run it from the terminal."),
    ("en-us", "Wait... what?! That's \"odd\" (really) - isn't it?"),
    ("en-us", "It costs $3.50, or 1,000 euros; about 3.14 times more."),
    ("en-us", "See e.g. the docs, i.e. README.md, at https://example.com/docs."),
    ("en-us", "Well—I think so… maybe; see: «this» and “that”."),
    ("en-us", "¿Qué? ¡Hola! {braces} [brackets]"),
    ("en-us", "Mr. Smith met Dr. Jones at 10:30 on 2026-09-29."),
    ("en-us", "C# and .NET 10, snake case, x = y + 2."),
    ("en-us", ", starts with a comma. Ends with a colon:"),
    ("en-us", "..."),
    ("en-us", "Title. First item. Second item."),
    ("en-us", LONG),
    ("en-gb", "The colour of the aluminium tomato, schedule."),
    ("es", "Hola, ¿cómo estás? Muy bien, gracias."),
    ("fr-fr", "Bonjour, comment ça va ? Très bien, merci !"),
    ("it", "Ciao, come stai? Tutto bene, grazie."),
    ("pt-br", "Olá, tudo bem? Estou ótimo, obrigado."),
    ("hi", "नमस्ते, आप कैसे हैं?"),
    ("ja", "こんにちは、お元気ですか？"),
    ("cmn", "你好，你好吗？我很好。"),
    ("es", "Me gusta el software open source."),
]

cases = []
for lang, text in PHONEME_CASES:
    phonemes = kokoro.tokenizer.phonemize(text, lang)
    # What create() goes on to do before the model sees it (kokoro_onnx __init__._prepare).
    joined = " ".join(phonemes.split())
    batches = kokoro._split_phonemes(joined) if joined else []
    cases.append({
        "lang": lang,
        "text": text,
        "phonemes": phonemes,
        "batches": [{"phonemes": b, "tokens": kokoro.tokenizer.tokenize(b), "pause": pause_after(b, 0.25, 0.1)}
                    for b in batches],
    })

AUDIO_CASES = [
    ("short", "af_heart", "en-us", 1.0, "Hello world"),
    ("sentences", "am_michael", "en-us", 1.0, "The build finished, and all the tests passed. Do you want me to commit this?"),
    ("fast", "am_michael", "en-us", 1.2, "Yes, you can run it from the terminal."),
    ("long", "bf_emma", "en-gb", 1.0, LONG),
    ("swedish-free", "ef_dora", "es", 1.0, "Hola, ¿cómo estás?"),
]
audio = []
for name, voice, lang, speed, text in AUDIO_CASES:
    samples, rate = kokoro.create(text, voice=voice, speed=speed, lang=lang)
    np.save(out / f"{name}.npy", samples.astype(np.float32))
    audio.append({"name": name, "voice": voice, "lang": lang, "speed": speed, "text": text,
                  "samples": int(len(samples)), "rate": rate})

(out / "kokoro-reference.json").write_text(
    json.dumps({"phonemes": cases, "audio": audio}, ensure_ascii=False, indent=1), encoding="utf-8")
print(f"{len(cases)} phoneme cases, {len(audio)} audio cases -> {out}")
