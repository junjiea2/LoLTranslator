using System.Text.RegularExpressions;

namespace LoLTranslator.App;

public static class RuntimeStructuredEventRenderer
{
    private static readonly (Regex Pattern, string Zh, string Ko, string En)[] Templates =
    {
        (Pattern(@"^\s*is\s+on\s+the\s+way[.!！。]?\s*$"), "正在路上", "가는 중", "On the way"),
        (Pattern(@"^\s*signals?\s+(?:that\s+)?(?:enemy|enemies|enernies)\s+are\s+(?:missing|rnissing)[.!！。]?\s*$"), "敌人消失", "적이 사라짐", "Enemies missing"),
        (Pattern(@"^\s*signals?\s+to\s+be\s+cautious[.!！。]?\s*$"), "小心！", "조심!", "Be cautious!"),
        (Pattern(@"^\s*is\s+asking\s+for\s+assistance[.!！。]?\s*$"), "请求协助", "지원 요청", "Requesting assistance"),
        (Pattern(@"^\s*is\s+asking\s+for\s+vision[.!！。]?\s*$"), "请求视野", "시야 요청", "Requesting vision"),
        (Pattern(@"^\s*enemy\s+has\s+vision\s+here[.!！。]?\s*$"), "敌方在此处有视野", "적이 이곳의 시야를 확보함", "Enemy has vision here"),
        (Pattern(@"^\s*enemy\s+has\s+vision[.!！。]?\s*$"), "敌方有视野", "적이 시야를 확보함", "Enemy has vision")
        ,(Pattern(@"^\s*signals?\s+to\s+all\s+in[.!！。]?\s*$"), "发起全力进攻信号", "총공격 신호", "Signals to all in")
        ,(Pattern(@"^\s*(?<target>.+?)\s+-\s+Alive[.!！。]?\s*$"), "{target} — 存活", "{target} - 생존", "{target} - Alive")
        ,(Pattern(@"^\s*(?<target>.+?)\s+(?:used\s+)?(?<spell>Flash|Exhaust|Ghost|Heal|Barrier|Smite|Ignite|Teleport|Cleanse)[.!！。]?\s*$"), "{target} 使用了 {spell}", "{target}: {spell} 사용", "{target} used {spell}")
        ,(Pattern(@"^\s*(?<target>.+?)\s+R\s*-\s*Ready[.!！。]?\s*$"), "{target} 的终极技能已就绪", "{target} 궁극기 준비됨", "{target} ultimate ready")
        ,(Pattern(@"^\s*Wait\s+For\s+(?<ability>.+?)[.!！。]?\s*$"), "等待 {ability}", "{ability} 대기", "Wait for {ability}")
        ,(Pattern(@"^\s*Jungle\s+Quest\s+Complete[.!！。]?\s*$"), "打野任务已完成", "정글 퀘스트 완료", "Jungle quest complete")
        ,(Pattern(@"^\s*has\s+stolen\s+(?<objective>(?:the\s+)?(?:Infernal|Cloud|Mountain|Ocean|Chemtech|Hextech|Elder)\s+(?:Drake|Dragon)|Rift\s+Herald|Baron\s+Nashor)[.!！。]?\s*$"), "抢到了 {objective}", "{objective} 스틸", "Stole {objective}")
        ,(Pattern(@"^\s*A\s+surrender\s+vote\s+has\s+been\s+started.*$"), "已发起投降投票", "항복 투표가 시작됨", "A surrender vote has started")
        ,(Pattern(@"^\s*(?<objective>(?:Infernal|Cloud|Mountain|Ocean|Chemtech|Hextech|Elder)\s+(?:Drake|Dragon)|Rift\s+Herald)\s*-\s*Spawning\s+in\s+(?<time>\d{1,2}:\d{2})[.!！。]?\s*$"), "{objective} 将在 {time} 后刷新", "{objective} {time} 후 생성", "{objective} spawning in {time}")
        ,(Pattern(@"^\s*signals?\s+(?:that\s+)?enemy\s+has\s+vision\s+here[.!！。]?\s*$"), "标记：敌方在此处有视野", "적이 이곳의 시야를 확보했다고 알림", "Signals enemy has vision here")
    };

    public static bool TryRenderPing(string body, string targetLocale, out string rendering)
    {
        foreach ((Regex pattern, string zh, string ko, string en) in Templates)
        {
            if (!pattern.IsMatch(body)) continue;
            string template = targetLocale.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zh
                : targetLocale.StartsWith("ko", StringComparison.OrdinalIgnoreCase) ? ko
                : en;
            rendering = pattern.GetGroupNames().Where(name => !int.TryParse(name, out _)).Aggregate(template,
                (text, name) => text.Replace("{" + name + "}", pattern.Match(body).Groups[name].Value.Trim(), StringComparison.Ordinal));
            return true;
        }
        rendering = string.Empty;
        return false;
    }

    private static Regex Pattern(string expression) => new(expression,
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
}
