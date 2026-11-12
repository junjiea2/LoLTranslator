# 聊天样本标注格式

`lol-chat-annotations.v0.1.jsonl` 保存“原始聊天样本如何被人工解释”，不替代 `data/lol-slang-v0.1.jsonl` 词典。

每条记录包含：

- `raw_text`：OCR 或人工抄录得到的原始短句，不能先改写成标准句。
- `sample_kind`：`synthetic_seed`、`real_chat` 或 `ocr_capture`。
- `labels`：一个样本可以有多个候选概念，每个标签带 `polarity`、slots 和标注者信心。
- `review.status`：`pending`、`verified` 或 `rejected`。
- `source_ids`：样本来源，必须能在主数据集的 `source` 记录中找到。

## 标注顺序

1. 先记录原文、语言、地区、版本和来源，不要先翻译。
2. 判断它是否表达一个已定义概念；不确定时使用 `uncertain`，不要强行归类。
3. 提取位置、目标、技能、地图资源和秒数等 slots。
4. 对容易误判的句子建立 `negative` 标签，并在 `notes` 中说明原因。
5. 只有经过人工复核并填写 `reviewer_id` 后，才允许把状态改成 `verified`。

当前示例是 `synthetic_seed`，仅验证字段和校验流程，不是真实玩家样本。
