#!/bin/bash
# 长跑监控：每 5 分钟把关键指标追加一行到 monitor.log，供人工抽查。只读，不干扰被测进程。
L=tools/ac03/results/ac03-20261120-112805.log
M=tools/ac03/monitor.log
for i in $(seq 1 100); do
  line=$(tail -1 "$L" 2>/dev/null | tr -d '\n')
  guard=$(grep -c "规模守卫" "$L" 2>/dev/null); guard=${guard:-0}
  focus=$(grep -c "本段没有写入" "$L" 2>/dev/null); focus=${focus:-0}
  unresp=$(grep -c "消息泵连续" "$L" 2>/dev/null); unresp=${unresp:-0}
  ents=$(python -c "import os;print(sum(len(f) for _,_,f in os.walk(r'D:/ac03/sandbox')))" 2>/dev/null)
  if [ -f "/d/ac03/app/data/logs/crash.log" ]; then crash=$(stat -c %s "/d/ac03/app/data/logs/crash.log"); else crash=0; fi
  printf '%s | %s | 条目 %s | 守卫 %s 焦点 %s 停顿 %s crashLog %sB\n' \
    "$(date +%H:%M:%S)" "$line" "$ents" "$guard" "$focus" "$unresp" "$crash" >> "$M"
  sleep 300
done
