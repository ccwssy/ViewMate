#!/usr/bin/env bash
# release-notes.sh V1.1.0
# release-notes.sh — 从 CHANGELOG.md 切片生成 Release notes（中文，累计变更口径）
#
# 用法：bash scripts/release-notes.sh <TAG> [CHANGELOG路径]
#   <TAG>           目标版本 tag，如 v1.2.19.0（必须是 CHANGELOG 表格行中的版本）
#   [CHANGELOG路径] 默认 CHANGELOG.md，相对仓库根解析
#
# 切片范围 =「上个正式版到当前」：从含 **$TAG** 的表格行起，到含 **$PREV** 的表格行之前；
#   PREV 由 `git describe --tags --abbrev=0 "$TAG^"` 取得；PREV 为空（TAG 不是真 tag，
#   或 TAG 已是最老 tag）时取「从 TAG 行到表格结束」。
# 每个条目输出：`### <版本> — <标题>` + 空行 + <正文>；条目之间空行分隔，文件以单个换行收尾。
# 版本与标题的 `**` 标记会去掉（`### vX — 标题` 需为纯文本）；正文里的 `**` 是 CHANGELOG
#   原文的强调标记，属内容本身，原样保留（GitHub 会渲染成粗体）。
# 摘要列（第 4 列「主要摘要」）：该行带非空摘要列时改输出 `### <版本> — 主要更新摘要` + 若干
#   `- ` bullet；摘要按全角分号 `；` 切分，段内原样输出（不清洗标点）。摘要列缺失或为空的
#   历史行仍输出标题 + 全量正文（回退，不报错）。
#   定位说明：表格列数由表头行动态解析。段数 = 表头列数 + 1 的标准行取第 4 列；正文里偶有未转义
#   的 `|`（如 v1.2.20.0 行反引号内的冲突键示例 `集 ItemId | 标记类型…`）会让段数多于列数 + 1，
#   此时按固定列号取会取到正文碎片，改取该行末段（摘要列恒为表格行最后一列）。段数不足
#   「表头列数 + 1」的行视为无摘要列（历史 3 列行）。
# 输出 UTF-8 无 BOM、LF 行尾。找不到条目或切出为空时输出回退文案，退出码仍为 0
#   （调用方 set -e 下不中断发版；资产上传才是主目的）。
set -uo pipefail

USAGE='用法: bash scripts/release-notes.sh <TAG> [CHANGELOG路径]'

if [[ $# -lt 1 || -z ${1:-} ]]; then
  echo "$USAGE" >&2
  echo '例: bash scripts/release-notes.sh v1.2.19.0' >&2
  exit 2
fi

TAG=$1
CHANGELOG=${2:-CHANGELOG.md}

# 相对路径按仓库根解析；不在 git 仓库内时退回脚本所在目录的上一级
if [[ $CHANGELOG != /* ]]; then
  ROOT=$(git rev-parse --show-toplevel 2>/dev/null || true)
  if [[ -z $ROOT ]]; then
    ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
  fi
  CHANGELOG="$ROOT/$CHANGELOG"
fi

# 上一正式版 tag：TAG 不是真 tag（或已是最老 tag）时 PREV 为空
PREV=$(git describe --tags --abbrev=0 "$TAG^" 2>/dev/null || true)

# 去掉字符串首尾空白（只按 ASCII 空白切，不碰多字节字符）
trim() {
  local s=$1
  s=${s#"${s%%[![:space:]]*}"}
  s=${s%"${s##*[![:space:]]}"}
  printf '%s' "$s"
}

OUT=''
if [[ -f $CHANGELOG ]]; then
  STARTED=0
  while IFS= read -r line || [[ -n $line ]]; do
    # 只处理表格行；一旦开始输出后遇到非表格行即表格结束
    if [[ $line != '|'* ]]; then
      ((STARTED)) && break
      continue
    fi

    IFS='|' read -r -a parts <<< "$line"
    # 表头行（`| 版本 | 日期 | … |`）只解析一次：记下表格列数，供判断某行是否带摘要列
    if [[ -z ${NCOLS:-} ]]; then
      ncols=0
      for ((i = 0; i < ${#parts[@]}; i++)); do
        [[ -n $(trim "${parts[i]}") ]] && ncols=$((ncols + 1))
      done
      ((ncols >= 3)) && NCOLS=$ncols
    fi
    # parts[1]=版本段 parts[2]=日期段 parts[3]=标题+正文段
    ver=${parts[1]:-}
    ver=${ver//\*/}
    ver=$(trim "$ver")
    # 跳过表头与分隔行（`|------|------|`）
    [[ $ver =~ ^v[0-9.]+$ ]] || continue

    if ((STARTED)); then
      # 命中上一正式版 → 区间结束
      if [[ -n $PREV && $ver == "$PREV" ]]; then
        break
      fi
    else
      if [[ $ver == "$TAG" ]]; then
        STARTED=1
      else
        continue
      fi
    fi

    # 摘要列：标准行取第 NCOLS 列；正文含未转义 `|` 的行段号被推多，末段仍是摘要列
    # （标准行末段即第 NCOLS 列，两者在「摘要列为空」时都 trim 成空 → 走回退分支）
    summary=''
    if [[ -n ${NCOLS:-} ]]; then
      nsegs=${#parts[@]}
      if ((nsegs == NCOLS + 1)); then
        summary=$(trim "${parts[NCOLS]:-}")
      elif ((nsegs > NCOLS + 1)); then
        summary=$(trim "${parts[nsegs - 1]:-}")
      fi
    fi

    # 摘要列按全角分号切成 bullet（段内原样，只切分、不清洗标点）；
    # 切成空（内容只有分号/空白）时视为无摘要列，走回退分支，绝不输出空条目
    bullets=''
    if [[ -n $summary ]]; then
      while IFS= read -r seg; do
        seg=$(trim "$seg")
        [[ -n $seg ]] || continue
        bullets+=$'\n'"- $seg"
      done <<< "${summary//；/$'\n'}"
    fi

    # 条目之间空行分隔；整个文件末尾只保留一个换行（文本文件惯例）
    if [[ -n $OUT ]]; then
      OUT+=$'\n\n'
    fi

    if [[ -n $bullets ]]; then
      # 摘要分支：`### <版本> — 主要更新摘要` + 空行 + 若干 `- ` bullet
      OUT+="### ${ver} — 主要更新摘要"
      OUT+=$'\n'"$bullets"
    else
      # 回退分支（无摘要列的历史行）：标题 + 全量正文，行为与 V1.0.0 一致
      content=${parts[3]:-}
      # 标题 = 首个 **…** 的内容；正文 = 第 3 段去掉该标记与其后首个全角破折号之后的部分
      title=''
      body=''
      if [[ $content =~ \*\*([^*]+)\*\* ]]; then
        title=${BASH_REMATCH[1]}
        rest=${content/"${BASH_REMATCH[0]}"/}
        if [[ $rest == *—* ]]; then
          body=${rest#*—}
        else
          body=$rest
        fi
        title=${title//\*\*/}
      elif [[ $content == *—* ]]; then
        # 无粗体标题的行：按全角破折号切分「标题 — 正文」
        title=${content%%—*}
        body=${content#*—}
      else
        # 无粗体、无破折号（早期行）：整段当标题，避免与正文重复
        title=$content
      fi
      title=$(trim "$title")
      body=$(trim "$body")

      OUT+="### ${ver} — ${title}"
      if [[ -n $body ]]; then
        OUT+=$'\n\n'"${body}"
      fi
    fi
  done < "$CHANGELOG"
fi

if [[ -z $OUT ]]; then
  # 回退：绝不输出空（否则线上 Release body 会变成空说明）
  printf '未在 CHANGELOG.md 找到 %s 条目；该版本可能早于 CHANGELOG 起始记录或版本号有误，请改看 git log：\n' "$TAG"
  printf 'git log --oneline %s\n' "$TAG"
  exit 0
fi

printf '%s\n' "$OUT"
exit 0
