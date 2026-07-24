#!/usr/bin/env sh
set -eu

umask 077

available_gib=$(df -g . | awk 'NR == 2 { print $4 }')
if [ "${available_gib}" -lt 10 ]; then
  echo "警告：可用磁盘空间少于 10 GiB。服务可以启动，但邮件归档会很快占满磁盘。"
fi

mkdir -p secrets .runtime/logs .runtime/data-protection-keys .runtime/postgres

if [ ! -f secrets/credential_encryption_key ]; then
  openssl rand -base64 32 > secrets/credential_encryption_key
fi

if [ ! -f .env ]; then
  postgres_password=$(openssl rand -base64 36 | tr -d '\n')
  cat > .env <<EOF
POSTGRES_PASSWORD=${postgres_password}
EOF
fi

echo "本机配置已创建。数据库密码和加密密钥仅保存在 .env 与 secrets/，均不会进入 Git。"
echo "启动前请先打开 Docker Desktop，然后执行：docker compose up -d --build"
