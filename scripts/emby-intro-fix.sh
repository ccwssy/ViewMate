#!/bin/sh
# emby-intro-fix.sh — 检测无 Intro 标记的剧集，从同季已有标记的集复制补打
# V1.0
# 用法: 
#   emby-intro-fix.sh                  # 默认 emby-test
#   emby-intro-fix.sh emby-test        # 测试服
#   emby-intro-fix.sh emby             # 主服
# 依赖: docker, python3 (宿主机)

INSTANCE="${1:-emby-test}"

case "$INSTANCE" in
  emby-test)
    DB_FILE="/mnt/data/docker-data/emby-test/config/data/library.db"
    CONTAINER="emby-test"
    ;;
  emby)
    DB_FILE="/mnt/data/docker-data/emby/data/library.db"
    CONTAINER="emby"
    ;;
  *)
    echo "Usage: $0 [emby-test|emby]"
    exit 1
    ;;
esac

echo "=== Emby Intro Fix: $INSTANCE ==="

# Stop container first (safe DB write)
docker stop "$CONTAINER" >/dev/null 2>&1

# Checkpoint WAL
python3 -c "
import sqlite3
conn = sqlite3.connect('$DB_FILE')
conn.execute('PRAGMA wal_checkpoint(TRUNCATE)')
conn.close()
" 2>/dev/null

# Find missing + fill from same-season reference
python3 << PYEOF 2>&1 | grep -v "^$"
import sqlite3, sys

db_path = "$DB_FILE"
conn = sqlite3.connect(db_path)
cur = conn.cursor()

# Find all series that have at least one episode with #ECS marker
cur.execute("""
    SELECT DISTINCT m.SeriesId FROM MediaItems m
    JOIN Chapters3 c ON c.ItemId = m.Id
    WHERE c.Name LIKE '%#ECS%'
""")
series_ids = [r[0] for r in cur.fetchall()]
print(f"Series with existing markers: {len(series_ids)}")

total_fixed = 0

for sid in series_ids:
    # Get all episodes in this series, their season
    cur.execute("""
        SELECT Id, Name, IndexNumber, ParentIndexNumber FROM MediaItems
        WHERE SeriesId=? AND Type=8 ORDER BY IndexNumber
    """, (sid,))
    episodes = cur.fetchall()
    if not episodes:
        continue

    # For each season (group by ParentIndexNumber)
    seasons = {}
    for ep in episodes:
        season_idx = ep[3] or 1
        seasons.setdefault(season_idx, []).append(ep)

    for season_idx, eps in seasons.items():
        # Find a reference episode in this season that HAS intro markers
        ref_id = None
        ref_start = None
        ref_end = None
        for ep in eps:
            cur.execute("""
                SELECT StartPositionTicks, Name FROM Chapters3
                WHERE ItemId=? AND Name LIKE '%#ECS%' ORDER BY StartPositionTicks
            """, (ep[0],))
            markers = cur.fetchall()
            if len(markers) >= 2:
                ref_id = ep[0]
                ref_start = markers[0][0]
                ref_end = markers[1][0]
                break

        if ref_id is None:
            continue  # No reference episode in this season

        # Check each episode for missing markers
        for ep in eps:
            cur.execute("""
                SELECT COUNT(*) FROM Chapters3
                WHERE ItemId=? AND Name LIKE '%#ECS%'
            """, (ep[0],))
            has = cur.fetchone()[0]
            if has >= 2:
                continue  # Already has markers

            # Missing! Write reference markers
            cur.execute("SELECT MAX(ChapterIndex) FROM Chapters3 WHERE ItemId=?", (ep[0],))
            max_idx = cur.fetchone()[0] or 0

            cur.execute("DELETE FROM Chapters3 WHERE ItemId=? AND Name LIKE '%#ECS%'", (ep[0],))
            cur.execute(
                "INSERT INTO Chapters3 (ItemId, ChapterIndex, StartPositionTicks, Name, MarkerType) VALUES (?, ?, ?, ?, ?)",
                (ep[0], max_idx + 1, ref_start, 'IntroStart#ECS', 1)
            )
            cur.execute(
                "INSERT INTO Chapters3 (ItemId, ChapterIndex, StartPositionTicks, Name, MarkerType) VALUES (?, ?, ?, ?, ?)",
                (ep[0], max_idx + 2, ref_end, 'IntroEnd#ECS', 2)
            )
            total_fixed += 1
            print(f"  Fixed: Series={sid} E{ep[2]} ({ep[1]})")

conn.commit()
conn.close()
print(f"Total missing markers fixed: {total_fixed}")
PYEOF

# Copy back to container (DB was modified while stopped)
CONFIG_DIR=$(dirname "$(dirname "$DB_FILE")")
# No need to copy back since we modified in-place on the mount

# Start container
docker start "$CONTAINER" >/dev/null 2>&1
echo "=== Done: $INSTANCE restarted ==="
