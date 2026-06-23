#!/bin/sh
# emby-pinyin-sync.sh — 为 Emby 新入库媒体注入拼音到 FTS5 索引
# 用法: 
#   emby-pinyin-sync.sh                   # 默认 emby-test
#   emby-pinyin-sync.sh emby-test          # 测试服
#   emby-pinyin-sync.sh emby               # 主服
# 依赖: docker, python:3.11-slim 镜像
# V2.2

INSTANCE="${1:-emby-test}"

case "$INSTANCE" in
  emby-test)
    DB_FILE="/mnt/data/docker-data/emby-test/config/data/library.db"
    CONFIG_DIR="/mnt/data/docker-data/emby-test/config"
    ;;
  emby)
    DB_FILE="/mnt/data/docker-data/emby/data/library.db"
    CONFIG_DIR="/mnt/data/docker-data/emby"
    ;;
  *)
    echo "Usage: $0 [emby-test|emby]"
    exit 1
    ;;
esac

echo "=== Emby Pinyin Sync: $INSTANCE ==="

# Step 1: Sidecar 更新 FTS 内容表
UPDATED=$(docker run --rm \
  -v "${CONFIG_DIR}:/config" \
  python:3.11-slim sh -c "
pip install -q pypinyin 2>/dev/null

python3 -c '
import sqlite3, pypinyin, re, math

db = sqlite3.connect(\"/config/data/library.db\")
cur = db.cursor()

cur.execute(\"\"\"SELECT count(*)
FROM fts_search9_content c
JOIN MediaItems mi ON c.id = mi.RowId
WHERE c.c0 NOT GLOB \"*[a-zA-Z]*\"
  AND c.c0 GLOB \"*[一-龥]*\"
  AND mi.Name NOT GLOB \"*Season*\" AND mi.Name NOT GLOB \"*Episode*\"
  AND mi.Name NOT GLOB \"*Media Folder*\"
\"\"\")
total = cur.fetchone()[0]

if total == 0:
    print(\"NO_NEW_ITEMS\")
    db.close()
    exit(0)

cur.execute(\"\"\"SELECT c.id, mi.Name
FROM fts_search9_content c
JOIN MediaItems mi ON c.id = mi.RowId
WHERE c.c0 NOT GLOB \"*[a-zA-Z]*\"
  AND c.c0 GLOB \"*[一-龥]*\"
  AND mi.Name NOT GLOB \"*Season*\" AND mi.Name NOT GLOB \"*Episode*\"
  AND mi.Name NOT GLOB \"*Media Folder*\"
\"\"\")
rows = cur.fetchall()

updated = 0
for row_id, name in rows:
    try:
        segs = [p[0].strip() for p in pypinyin.pinyin(name, style=pypinyin.NORMAL) if p[0].strip().isalpha()]
        spaced = \" \".join(segs)
        connected = \"\".join(segs)
        new_c0 = f\"{name} {spaced} {connected}\"
        cur.execute(\"UPDATE fts_search9_content SET c0 = ? WHERE id = ?\", (new_c0, row_id))
        updated += 1
        if updated <= 5 or updated % 500 == 0 or updated == total:
            pct = math.floor(updated * 100 / total)
            print(f\"  [{pct}%] {updated}/{total}\")
    except Exception as e:
        print(f\"  X {name}: {e}\")

db.commit()
print(f\"DONE:{updated}\")
db.close()
'
" 2>/dev/null)

echo "$UPDATED"

# Step 2: 宿主机 Python 重建 FTS 索引
UPDATED_NUM=$(echo "$UPDATED" | grep -oP '(?<=DONE:)\d+' || true)
if [ -n "$UPDATED_NUM" ] && [ "$UPDATED_NUM" -gt 0 ] 2>/dev/null; then
  echo "Rebuilding FTS index..."
  python3 -c "
import sqlite3
db = sqlite3.connect('$DB_FILE')
db.execute(\"INSERT INTO fts_search9(fts_search9) VALUES('rebuild')\")
db.commit()
db.close()
print('FTS index rebuilt')
"
fi
