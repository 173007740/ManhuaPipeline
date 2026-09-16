OUT=/home/featurize/ComfyUI/output/video
echo "=== latest outputs ==="
ls -lat "$OUT" 2>/dev/null | head -14
echo
echo "=== probe ==="
PY=/environment/miniconda3/envs/comfyui/bin/python
if [ ! -x "$PY" ]; then PY=python3; fi
"$PY" - <<'PY'
import os
p = "/home/featurize/ComfyUI/output/video/H3-turbo_v4_8 step_Ref_00025_.mp4"
if not os.path.exists(p):
    print("MISSING:", p)
    raise SystemExit
try:
    import av
except Exception as e:
    print("no av:", e)
    raise SystemExit
c = av.open(p)
s = c.streams.video[0]
dur = None
try:
    if s.duration is not None:
        dur = float(s.duration * s.time_base)
except Exception:
    pass
print("file      :", os.path.basename(p))
print("size      : %sx%s" % (s.width, s.height))
print("frames    :", s.frames)
print("fps       :", s.average_rate)
print("duration  : %s s" % dur)
print("bytes     :", os.path.getsize(p))
PY
