pkill -f 'ts.log' 2>/dev/null
sleep 1
rm -f /tmp/ts.log
nohup bash -c '
LOG=/home/featurize/.apphub/comfyui.log
while true; do
  sz=$(stat -c %s "$LOG" 2>/dev/null)
  gpu=$(nvidia-smi --query-gpu=utilization.gpu,memory.used --format=csv,noheader,nounits 2>/dev/null | tr -d " ")
  last=$(tail -c 400 "$LOG" 2>/dev/null | tr "\r" "\n" | grep -v "^$" | tail -1 | cut -c1-90)
  printf "%s size=%s gpu=%s | %s\n" "$(date +%H:%M:%S)" "$sz" "$gpu" "$last" >> /tmp/ts.log
  sleep 5
done
' >/dev/null 2>&1 &
sleep 1
echo "watcher2 started"
date +%H:%M:%S
