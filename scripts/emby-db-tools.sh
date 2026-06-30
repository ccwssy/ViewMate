#!/bin/bash
# emby-db-tools.sh — Emby 数据库维护工具集
# 用法:
#   ./emby-db-tools.sh check-pinyin [容器名]  — 验证拼音录入
#   ./emby-db-tools.sh check-integrity [容器名] — 验证数据库完整性
#   ./emby-db-tools.sh repair [容器名]         — 修复损坏的数据库
#
# 默认容器名: emby，可通过参数覆盖

set -e

CONTAINER="${2:-emby}"
DB_PATH="/mnt/data/docker-data/${CONTAINER}/data/library.db"

check_container() {
    if ! docker ps --format '{{.Names}}' | grep -qx "$CONTAINER"; then
        echo "错误: 容器 '$CONTAINER' 未运行"
        exit 1
    fi
}

host_sqlite() {
    sqlite3 "$DB_PATH" "$1" 2>&1
}

cmd_check_pinyin() {
    check_container
    echo "=== 拼音录入验证 ==="

    local total
    total=$(host_sqlite "SELECT COUNT(*) FROM fts_search9;")
    echo "FTS 记录总数: $total"

    if [ "$total" -gt 0 ]; then
        echo ""
        echo "测试拼音搜索 (dianying):"
        host_sqlite "SELECT Name FROM fts_search9 WHERE fts_search9 MATCH 'dianying' LIMIT 5;"
        echo ""
        echo "FTS 内容样例:"
        host_sqlite "SELECT substr(c0,1,120) FROM fts_search9_content LIMIT 1;"
    else
        echo "警告: FTS 为空，未录入拼音数据。"
    fi
}

cmd_check_integrity() {
    check_container
    echo "=== 数据库完整性检查 ==="
    echo "数据库: $DB_PATH"
    echo ""

    local result
    result=$(host_sqlite "PRAGMA integrity_check;")
    echo "结果: $result"

    if [ "$result" = "ok" ]; then
        echo "✓ 数据库完好"
        return 0
    else
        echo "✗ 数据库已损坏！使用 '$0 repair' 修复"
        return 1
    fi
}

cmd_repair() {
    check_container
    echo "=== 数据库修复 ==="

    # 确认
    echo "即将修复: $DB_PATH"
    echo -n "确认？(y/N): "
    read -r confirm
    if [ "$confirm" != "y" ] && [ "$confirm" != "Y" ]; then
        echo "已取消"
        exit 1
    fi

    # 1. 停容器
    echo "[1/8] 停止容器 $CONTAINER..."
    docker stop "$CONTAINER"

    # 2. 备份
    local backup="${DB_PATH}.bak-$(date +%Y%m%d)"
    echo "[2/8] 备份到 $backup..."
    cp "$DB_PATH" "$backup"

    # 3. 导出可恢复数据
    local sql="/tmp/emby-db-recover-$$.sql"
    echo "[3/8] 导出可恢复数据..."
    sqlite3 "$DB_PATH" ".recover" > "$sql"
    local sql_size
    sql_size=$(wc -c < "$sql")
    echo "      导出 $sql_size 字节"

    # 4. 重建数据库
    local recovered="${DB_PATH}.recovered"
    echo "[4/8] 重建数据库..."
    sqlite3 "$recovered" < "$sql"
    rm -f "$sql"

    # 5. 重建 FTS 虚拟表
    echo "[5/8] 重建 FTS5 虚拟表..."
    sqlite3 "$recovered" "
        DROP TABLE IF EXISTS fts_search9;
        DROP TABLE IF EXISTS fts_search9_data;
        DROP TABLE IF EXISTS fts_search9_idx;
        DROP TABLE IF EXISTS fts_search9_content;
        DROP TABLE IF EXISTS fts_search9_docsize;
        DROP TABLE IF EXISTS fts_search9_config;
    " 2>/dev/null
    sqlite3 "$recovered" "
        CREATE VIRTUAL TABLE fts_search9 USING FTS5(
            Name, OriginalTitle, SeriesName, Album,
            tokenize='unicode61 remove_diacritics 2',
            prefix='1 2 3 4'
        );
    "

    # 6. 验证
    echo "[6/8] 验证新库..."
    local result
    result=$(sqlite3 "$recovered" "PRAGMA integrity_check;")
    if [ "$result" != "ok" ]; then
        echo "错误: 修复后的数据库 integrity_check 失败: $result"
        echo "备份保留在: $backup"
        docker start "$CONTAINER" 2>/dev/null || true
        exit 1
    fi
    echo "      ✓ integrity_check: ok"
    echo "      FTS $(sqlite3 "$recovered" 'SELECT COUNT(*) FROM fts_search9;') 条 (空，插件将自动重建)"

    # 7. 替换
    echo "[7/8] 替换原库..."
    mv "$recovered" "$DB_PATH"

    # 8. 启动
    echo "[8/8] 启动容器 $CONTAINER..."
    docker start "$CONTAINER"
    echo ""
    echo "✓ 修复完成。插件将在后台自动重建拼音数据（约30-60秒），首页可正常加载。"
}

case "${1:-help}" in
    check-pinyin)   cmd_check_pinyin ;;
    check-integrity) cmd_check_integrity ;;
    repair)         cmd_repair ;;
    help|--help|-h)
        echo "Emby 数据库维护工具"
        echo ""
        echo "用法:"
        echo "  $0 check-pinyin [容器名]    验证拼音录入"
        echo "  $0 check-integrity [容器名] 验证数据库完整性"
        echo "  $0 repair [容器名]          修复损坏的数据库"
        echo "  $0 help                     显示此帮助"
        echo ""
        echo "默认容器名: emby"
        ;;
    *)
        echo "未知命令: $1"
        echo "用法: $0 help"
        exit 1
        ;;
esac
