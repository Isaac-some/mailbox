#!/usr/bin/env sh
set -eu

legacy_root=Data
runtime_root=.runtime

if [ ! -d "$legacy_root/postgres" ]; then
  echo "未发现旧版浏览器数据，无需迁移。"
  exit 0
fi

if [ -e "$runtime_root/postgres" ] || [ -e "$runtime_root/data-protection-keys" ] || [ -e "$runtime_root/logs" ]; then
  echo "目标运行目录已存在；为避免覆盖数据，未执行迁移。"
  exit 1
fi

if [ -e "$legacy_root/postgres/postmaster.pid" ]; then
  echo "旧 PostgreSQL 看起来仍在运行。先执行 docker compose down，确认停止后再迁移。"
  exit 1
fi

mkdir -p "$runtime_root"
for directory in postgres data-protection-keys logs; do
  if [ -d "$legacy_root/$directory" ]; then
    mkdir -p "$runtime_root/$directory"
    (cd "$legacy_root/$directory" && tar cf - .) | (cd "$runtime_root/$directory" && tar xpf -)
  else
    mkdir -p "$runtime_root/$directory"
  fi
done

echo "旧浏览器运行数据已复制到 $runtime_root/。原始 Data/ 内容未修改。"
