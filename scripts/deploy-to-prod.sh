#!/usr/bin/env bash
# ViewMate 部署到正式服（emby 容器）—— 维护者手动执行，不自动跑
# 用法: bash deploy-to-prod.sh [--yes]      # --yes 跳过交互确认
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILD_DIR="$REPO/ViewMate/bin/Release/net6.0"
PROD_CONTAINER="emby"
PLUGIN_DIR="/config/plugins"
EMBY_HOST="${EMBY_HOST:-127.0.0.1:8095}"
BACKUP_TAG="$(date +%Y%m%d-%H%M%S)"

cd "$REPO"
VERSION="$(grep -oP '<Version>\K[^<]+' ViewMate/ViewMate.csproj | head -1)"
echo "== ViewMate 部署到正式服 =="
echo "目标版本: $VERSION | 正式服容器: $PROD_CONTAINER"

# ── 0. 前置检查 + 取「验证过的产物」──
# 本项目 Deterministic=false，同样源码两次构建 sha 不同 ⇒ 必须部署「测试实例上验证过的那一份」
TEST_CONTAINER="emby-test"
SRC_DIR="${DEPLOY_SRC_DIR:-${TMPDIR:-/tmp}/viewmate-deploy-src}"
mkdir -p "$SRC_DIR"
docker cp "$TEST_CONTAINER:$PLUGIN_DIR/ViewMate.dll" "$SRC_DIR/ViewMate.dll" \
  || { echo "❌ 无法从 $TEST_CONTAINER 导出已验证产物"; exit 1; }
LOCAL_SHA="$(sha256sum "$SRC_DIR/ViewMate.dll" | cut -c1-16)"
TEST_SHA="$(docker exec "$TEST_CONTAINER" sha256sum "$PLUGIN_DIR/ViewMate.dll" | cut -c1-16)"
[ "$LOCAL_SHA" = "$TEST_SHA" ] || { echo "❌ 导出校验失败（$LOCAL_SHA ≠ $TEST_SHA）"; exit 1; }
echo "部署源 = $TEST_CONTAINER 上验证过的产物，sha256 前缀: $LOCAL_SHA"
CUR_VER="$(docker logs "$PROD_CONTAINER" --since 72h 2>&1 | grep -oE 'Loading ViewMate, Version=[0-9.]+' | tail -1 || true)"
echo "正式服当前版本: ${CUR_VER:-未知}"

if [ "${1:-}" != "--yes" ]; then
  read -r -p "确认部署到正式服？（会重启 $PROD_CONTAINER，中断正在观看的播放）[y/N] " ans
  [ "$ans" = "y" ] || { echo "已取消"; exit 0; }
fi

# ── 1. 备份当前正式服产物（用于回滚）──
echo "── 备份当前插件产物 ──"
docker exec "$PROD_CONTAINER" sh -c "cp -a $PLUGIN_DIR/ViewMate.dll $PLUGIN_DIR/ViewMate.dll.bak.$BACKUP_TAG" && \
  echo "已备份 → $PLUGIN_DIR/ViewMate.dll.bak.$BACKUP_TAG"
ROLLBACK=1

rollback() {
  echo "❗ 部署失败，执行回滚..."
  docker exec "$PROD_CONTAINER" sh -c "cp -a $PLUGIN_DIR/ViewMate.dll.bak.$BACKUP_TAG $PLUGIN_DIR/ViewMate.dll" || true
  docker restart "$PROD_CONTAINER" >/dev/null || true
  sleep 25
  echo "已回滚到部署前版本（备份文件保留：ViewMate.dll.bak.$BACKUP_TAG）"
  exit 1
}

# ── 2. 停 → 复制 → 启 ──
echo "── 部署新产物 ──"
docker stop "$PROD_CONTAINER" >/dev/null
docker cp "$SRC_DIR/ViewMate.dll" "$PROD_CONTAINER:$PLUGIN_DIR/ViewMate.dll" || rollback
if [ -d "$BUILD_DIR/zh" ]; then docker cp "$BUILD_DIR/zh/." "$PROD_CONTAINER:$PLUGIN_DIR/zh/" || rollback; fi
if [ -d "$BUILD_DIR/zh-hant" ]; then docker cp "$BUILD_DIR/zh-hant/." "$PROD_CONTAINER:$PLUGIN_DIR/zh-hant/" || rollback; fi
docker start "$PROD_CONTAINER" >/dev/null
echo "容器已重启，等待插件加载（40 秒）..."
sleep 40

# ── 3. 验证 ──
echo "── 验证 ──"
DEPLOYED_SHA="$(docker exec "$PROD_CONTAINER" sha256sum "$PLUGIN_DIR/ViewMate.dll" | cut -c1-16)"
LOG_LINE="$(docker logs "$PROD_CONTAINER" --since 2m 2>&1 | grep -oE "Loading ViewMate, Version=[0-9.]+" | tail -1 || true)"
ENTRY_LINE="$(docker logs "$PROD_CONTAINER" --since 2m 2>&1 | grep -oE "Entry point completed: ViewMate[A-Za-z.]*" | tail -1 || true)"
ERR_CNT="$(docker logs "$PROD_CONTAINER" --since 2m 2>&1 | grep -icE "观影助手.*(error|exception)" || true)"

echo "  部署后 DLL sha 前缀: $DEPLOYED_SHA （本地 $LOCAL_SHA）"
echo "  日志: ${LOG_LINE:-（未见 Loading 行）} | ${ENTRY_LINE:-（未见 Entry point 行）}"
echo "  插件相关 Error/Exception 行数: $ERR_CNT"

if [ "$DEPLOYED_SHA" != "$LOCAL_SHA" ] || [ -z "$LOG_LINE" ] || [ "$ERR_CNT" != "0" ]; then
  rollback
fi

# thumb 图校验（16:9 定稿图）
THUMB_CODE="$(curl -s -o /dev/null -w '%{http_code}' -m 15 "http://${EMBY_HOST}/emby/Plugins/63c322b7-a371-41a3-b11f-04f8418b37d8/thumb" || true)"
echo "  插件主图 API: http=$THUMB_CODE（期望 200）"

ROLLBACK=0
echo
echo "✅ 部署完成：$VERSION（正式服）"
echo "   回滚命令（若需）：docker exec $PROD_CONTAINER cp -a $PLUGIN_DIR/ViewMate.dll.bak.$BACKUP_TAG $PLUGIN_DIR/ViewMate.dll && docker restart $PROD_CONTAINER"
echo "   注意：部署后首次播放会重建该集的章节缓存；手动标记由自愈（+150s / 每 10 分钟）保护。"
