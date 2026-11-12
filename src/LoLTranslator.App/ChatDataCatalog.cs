using System.IO;
using System.Text.Json;

namespace LoLTranslator.App;

/// <summary>
/// 从 JSONL 读取数据层记录，并在进入运行时前完成引用校验。
/// 该类不依赖 OCR，也不负责翻译生成。
/// </summary>
public static class LolSlangDatasetLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static LolSlangDataset LoadFromJsonl(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("数据集路径不能为空。", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("找不到 LoL 黑话数据集。", filePath);
        }

        SlangDatasetHeader? header = null;
        var concepts = new List<SlangConcept>();
        var expressions = new List<SlangExpression>();
        var templates = new List<SlangTemplate>();
        var entities = new List<GameEntity>();
        var relations = new List<SemanticRelation>();
        var examples = new List<TextExample>();
        var sources = new List<DataSource>();

        int lineNumber = 0;

        foreach (string line in File.ReadLines(filePath))
        {
            lineNumber++;

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;

                if (!root.TryGetProperty("record_type", out JsonElement recordTypeElement) ||
                    recordTypeElement.ValueKind != JsonValueKind.String)
                {
                    throw new FormatException("缺少 record_type。\n");
                }

                string recordType = recordTypeElement.GetString()!;

                switch (recordType)
                {
                    case "dataset":
                        header = Deserialize<SlangDatasetHeader>(line, lineNumber);
                        break;
                    case "concept":
                        concepts.Add(Deserialize<SlangConcept>(line, lineNumber));
                        break;
                    case "expression":
                        expressions.Add(Deserialize<SlangExpression>(line, lineNumber));
                        break;
                    case "template":
                        templates.Add(Deserialize<SlangTemplate>(line, lineNumber));
                        break;
                    case "entity":
                        entities.Add(Deserialize<GameEntity>(line, lineNumber));
                        break;
                    case "relation":
                        relations.Add(Deserialize<SemanticRelation>(line, lineNumber));
                        break;
                    case "example":
                        examples.Add(Deserialize<TextExample>(line, lineNumber));
                        break;
                    case "source":
                        sources.Add(Deserialize<DataSource>(line, lineNumber));
                        break;
                    default:
                        throw new FormatException($"未知 record_type '{recordType}'。");
                }
            }
            catch (JsonException exception)
            {
                throw new FormatException($"第 {lineNumber} 行 JSON 无效。", exception);
            }
            catch (FormatException exception)
            {
                throw new FormatException($"第 {lineNumber} 行数据无效：{exception.Message}", exception);
            }
        }

        if (header is null)
        {
            throw new InvalidDataException("数据集缺少 dataset 头记录。");
        }

        var dataset = new LolSlangDataset(
            header.SchemaVersion,
            header.DatasetId,
            header.DatasetVersion,
            concepts,
            expressions,
            templates,
            entities,
            relations,
            examples,
            sources);

        IReadOnlyList<string> errors = LolSlangDatasetValidator.Validate(dataset);

        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                "LoL 黑话数据集引用校验失败：" +
                Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(error => $"- {error}")));
        }

        return dataset;
    }

    private static T Deserialize<T>(string json, int lineNumber)
    {
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new FormatException($"第 {lineNumber} 行无法转换为 {typeof(T).Name}。");
    }
}

public sealed class LolSlangCatalog
{
    private readonly Dictionary<string, SlangConcept> _concepts;
    private readonly IReadOnlyList<GameEntity> _entities;
    private readonly IReadOnlyList<SlangExpression> _expressions;
    private readonly IReadOnlyList<SlangTemplate> _templates;

    private LolSlangCatalog(LolSlangDataset dataset)
    {
        Dataset = dataset;
        _concepts = dataset.Concepts.ToDictionary(
            concept => concept.ConceptId,
            StringComparer.OrdinalIgnoreCase);
        _expressions = dataset.Expressions;
        _templates = dataset.Templates;
        _entities = dataset.Entities;
    }

    public LolSlangDataset Dataset { get; }

    public static LolSlangCatalog LoadFromJsonl(string filePath)
    {
        return new LolSlangCatalog(
            LolSlangDatasetLoader.LoadFromJsonl(filePath));
    }

    public SlangConcept? FindConcept(string conceptId)
    {
        return _concepts.TryGetValue(conceptId, out SlangConcept? concept)
            ? concept
            : null;
    }

    public GameEntity? FindEntityByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return _entities.FirstOrDefault(entity =>
            entity.CanonicalNames.Values.Any(value =>
                string.Equals(value, name, StringComparison.OrdinalIgnoreCase)) ||
            entity.Aliases.Any(alias =>
                string.Equals(alias, name, StringComparison.OrdinalIgnoreCase)));
    }

    public IReadOnlyList<SlangExpression> FindExpressions(
        string conceptId,
        string? locale = null,
        bool includeDraft = true)
    {
        return _expressions
            .Where(expression =>
                string.Equals(expression.ConceptId, conceptId, StringComparison.OrdinalIgnoreCase) &&
                (locale is null || string.Equals(expression.Locale, locale, StringComparison.OrdinalIgnoreCase)) &&
                (includeDraft || string.Equals(expression.Status, "verified", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    public IReadOnlyList<SlangTemplate> FindTemplates(
        string conceptId,
        string locale,
        bool includeDraft = true)
    {
        return _templates
            .Where(template =>
                string.Equals(template.ConceptId, conceptId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(template.Locale, locale, StringComparison.OrdinalIgnoreCase) &&
                (includeDraft || string.Equals(template.Status, "verified", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }
}
