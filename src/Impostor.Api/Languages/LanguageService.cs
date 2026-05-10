using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Impostor.Api.Innersloth;
using Microsoft.Extensions.Logging;

namespace Impostor.Api.Languages;

public sealed class LanguageService
{
    private static readonly string LangDir = Path.Combine(Directory.GetCurrentDirectory(), "Languages");
    public readonly ILogger<LanguageService> _logger;
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _tables = new();
    private static readonly string FallbackLang = "en";

    private static readonly Dictionary<Language, string> LangCodeMap = new()
    {
        [Language.English] = "en",
        [Language.Latam] = "es",
        [Language.Brazilian] = "pt_BR",
        [Language.Portuguese] = "pt",
        [Language.Korean] = "ko",
        [Language.Russian] = "ru",
        [Language.Dutch] = "nl",
        [Language.Filipino] = "fil",
        [Language.French] = "fr",
        [Language.German] = "de",
        [Language.Italian] = "it",
        [Language.Japanese] = "ja",
        [Language.Spanish] = "es",
        [Language.SChinese] = "zh",
        [Language.TChinese] = "zh",
        [Language.Irish] = "ga",
    };

    public LanguageService(ILogger<LanguageService> logger)
    {
        _logger = logger;
        EnsureDefaults();
        LoadAll();
    }

    public LanguageString Get(string key, Language language = Language.English)
    {
        var code = LangCodeMap.TryGetValue(language, out var c) ? c : FallbackLang;
        var text = Lookup(key, code) ?? Lookup(key, FallbackLang) ?? key;
        return new LanguageString(text);
    }

    public LanguageString Get(string key, string langCode)
    {
        var text = Lookup(key, langCode) ?? Lookup(key, FallbackLang) ?? key;
        return new LanguageString(text);
    }

    public void Reload()
    {
        _tables.Clear();
        LoadAll();
        _logger.LogInformation("[Language] Reloaded all language files.");
    }

    private string? Lookup(string key, string langCode)
    {
        return _tables.TryGetValue(langCode, out var table) && table.TryGetValue(key, out var val)
            ? val
            : null;
    }

    private void LoadAll()
    {
        if (!Directory.Exists(LangDir)) return;
        foreach (var file in Directory.GetFiles(LangDir, "*.json"))
        {
            var code = Path.GetFileNameWithoutExtension(file);
            try
            {
                var json = File.ReadAllText(file);
                var table = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                    ?? new Dictionary<string, string>();
                _tables[code] = table;
                _logger.LogDebug("[Language] Loaded {Code} ({Count} keys)", code, table.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Language] Failed to load {File}", file);
            }
        }
        _logger.LogInformation("[Language] Loaded {Count} language file(s).", _tables.Count);
    }

    private void EnsureDefaults()
    {
        Directory.CreateDirectory(LangDir);
        WriteIfMissing("en.json", DefaultEn);
        WriteIfMissing("zh_CN.json", DefaultZhCn);
        WriteIfMissing("zh_TW.json", DefaultZhTw);
        WriteIfMissing("ko.json", DefaultKo);
        WriteIfMissing("ru.json", DefaultRu);
        WriteIfMissing("de.json", DefaultDe);
        WriteIfMissing("fr.json", DefaultFr);
        WriteIfMissing("ja.json", DefaultJa);
        WriteIfMissing("pt.json", DefaultPt);
        WriteIfMissing("pt_BR.json", DefaultPtBr);
    }

    private void WriteIfMissing(string fileName, string content)
    {
        var path = Path.Combine(LangDir, fileName);
        if (!File.Exists(path))
            File.WriteAllText(path, content);
    }

    // Language Tranlations used AI.
    // Any Pr fix grammer wrongs is OK!
    private const string DefaultEn = """
{
  "command.unknown": "Unknown command /{0}. Type /help for a list.",
  "command.error": "An error occurred while executing /{0}.",
  "command.usage": "Usage: /{0}",
  "command.help.list": "=== Commands ===",
  "command.help.entry": "/{0} — {1}",
  "command.help.unknown": "Unknown command: /{0}",
  "command.help.aliases": "Aliases: {0}",
  "command.note.host_only": "Only the host can use /note.",
  "command.note.cleared": "Note cleared.",
  "command.note.set": "Note set: {0}",
  "command.color.invalid": "Invalid color ID. Valid range: 0–17.",
  "command.color.set": "Color changed to {0} ({1}).",
  "command.title.too_long": "Title too long. Max 12 characters.",
  "command.title.cleared": "Title removed.",
  "command.title.set": "Title set: {0}",
  "welcome.join": "Welcome, {0}! Friend code: {1} | Room: {2}"
}
""";

    private const string DefaultZhCn = """
{
  "command.unknown": "未知指令 /{0}。输入 /help 查看列表。",
  "command.error": "执行 /{0} 时发生错误。",
  "command.usage": "用法：/{0}",
  "command.help.list": "=== 指令列表 ===",
  "command.help.entry": "/{0} — {1}",
  "command.help.unknown": "未知指令：/{0}",
  "command.help.aliases": "别名：{0}",
  "command.note.host_only": "只有房主可以使用 /note。",
  "command.note.cleared": "备注已清除。",
  "command.note.set": "备注已设置：{0}",
  "command.color.invalid": "无效颜色 ID，范围：0–17。",
  "command.color.set": "颜色已更改为 {0}（{1}）。",
  "command.title.too_long": "头衔过长，最多 12 个字符。",
  "command.title.cleared": "头衔已移除。",
  "command.title.set": "头衔已设置：{0}",
  "welcome.join": "欢迎，{0}！好友码：{1} | 房间：{2}"
}
""";

    private const string DefaultZhTw = """
{
  "command.unknown": "未知指令 /{0}。輸入 /help 查看列表。",
  "command.error": "執行 /{0} 時發生錯誤。",
  "command.usage": "用法：/{0}",
  "command.help.list": "=== 指令列表 ===",
  "command.help.entry": "/{0} — {1}",
  "command.help.unknown": "未知指令：/{0}",
  "command.help.aliases": "別名：{0}",
  "command.note.host_only": "只有房主可以使用 /note。",
  "command.note.cleared": "備註已清除。",
  "command.note.set": "備註已設置：{0}",
  "command.color.invalid": "無效顏色 ID，範圍：0–17。",
  "command.color.set": "顏色已更改為 {0}（{1}）。",
  "command.title.too_long": "頭銜過長，最多 12 個字符。",
  "command.title.cleared": "頭銜已移除。",
  "command.title.set": "頭銜已設置：{0}",
  "welcome.join": "歡迎，{0}！好友碼：{1} | 房間：{2}"
}
""";

    private const string DefaultKo = """
{
  "command.unknown": "알 수 없는 명령어 /{0}. /help를 입력하여 목록을 확인하세요.",
  "command.error": "/{0} 실행 중 오류가 발생했습니다.",
  "command.usage": "사용법: /{0}",
  "command.help.list": "=== 명령어 목록 ===",
  "command.help.entry": "/{0} — {1}",
  "command.help.unknown": "알 수 없는 명령어: /{0}",
  "command.help.aliases": "별칭: {0}",
  "command.note.host_only": "/note는 방장만 사용할 수 있습니다.",
  "command.note.cleared": "메모가 삭제되었습니다.",
  "command.note.set": "메모 설정됨: {0}",
  "command.color.invalid": "잘못된 색상 ID. 유효 범위: 0–17.",
  "command.color.set": "색상이 {0}({1})으로 변경되었습니다.",
  "command.title.too_long": "칭호가 너무 깁니다. 최대 12자.",
  "command.title.cleared": "칭호가 제거되었습니다.",
  "command.title.set": "칭호 설정됨: {0}",
  "welcome.join": "환영합니다, {0}! 친구 코드: {1} | 방: {2}"
}
""";

    private const string DefaultRu = """
{
  "command.unknown": "Неизвестная команда /{0}. Введите /help для списка.",
  "command.error": "Ошибка при выполнении /{0}.",
  "command.usage": "Использование: /{0}",
  "command.help.list": "=== Команды ===",
  "command.help.entry": "/{0} — {1}",
  "command.help.unknown": "Неизвестная команда: /{0}",
  "command.help.aliases": "Псевдонимы: {0}",
  "command.note.host_only": "Только хост может использовать /note.",
  "command.note.cleared": "Заметка удалена.",
  "command.note.set": "Заметка установлена: {0}",
  "command.color.invalid": "Неверный ID цвета. Допустимый диапазон: 0–17.",
  "command.color.set": "Цвет изменён на {0} ({1}).",
  "command.title.too_long": "Титул слишком длинный. Максимум 12 символов.",
  "command.title.cleared": "Титул удалён.",
  "command.title.set": "Титул установлен: {0}",
  "welcome.join": "Добро пожаловать, {0}! Код друга: {1} | Комната: {2}"
}
""";

    private const string DefaultDe = """
{
  "command.unknown": "Unbekannter Befehl /{0}. Tippe /help für eine Liste.",
  "command.error": "Fehler beim Ausführen von /{0}.",
  "command.usage": "Verwendung: /{0}",
  "command.help.list": "=== Befehle ===",
  "command.help.entry": "/{0} — {1}",
  "command.help.unknown": "Unbekannter Befehl: /{0}",
  "command.help.aliases": "Aliase: {0}",
  "command.note.host_only": "Nur der Host kann /note verwenden.",
  "command.note.cleared": "Notiz gelöscht.",
  "command.note.set": "Notiz gesetzt: {0}",
  "command.color.invalid": "Ungültige Farb-ID. Gültiger Bereich: 0–17.",
  "command.color.set": "Farbe geändert zu {0} ({1}).",
  "command.title.too_long": "Titel zu lang. Maximal 12 Zeichen.",
  "command.title.cleared": "Titel entfernt.",
  "command.title.set": "Titel gesetzt: {0}",
  "welcome.join": "Willkommen, {0}! Freundescode: {1} | Raum: {2}"
}
""";

    private const string DefaultFr = """
{
  "command.unknown": "Commande inconnue /{0}. Tapez /help pour la liste.",
  "command.error": "Erreur lors de l'exécution de /{0}.",
  "command.usage": "Utilisation : /{0}",
  "command.help.list": "=== Commandes ===",
  "command.help.entry": "/{0} — {1}",
  "command.help.unknown": "Commande inconnue : /{0}",
  "command.help.aliases": "Alias : {0}",
  "command.note.host_only": "Seul l'hôte peut utiliser /note.",
  "command.note.cleared": "Note effacée.",
  "command.note.set": "Note définie : {0}",
  "command.color.invalid": "ID de couleur invalide. Plage valide : 0–17.",
  "command.color.set": "Couleur changée en {0} ({1}).",
  "command.title.too_long": "Titre trop long. Maximum 12 caractères.",
  "command.title.cleared": "Titre supprimé.",
  "command.title.set": "Titre défini : {0}",
  "welcome.join": "Bienvenue, {0} ! Code ami : {1} | Salle : {2}"
}
""";

    private const string DefaultJa = """
{
  "command.unknown": "不明なコマンド /{0}。/help でリストを確認してください。",
  "command.error": "/{0} の実行中にエラーが発生しました。",
  "command.usage": "使い方：/{0}",
  "command.help.list": "=== コマンド一覧 ===",
  "command.help.entry": "/{0} — {1}",
  "command.help.unknown": "不明なコマンド：/{0}",
  "command.help.aliases": "エイリアス：{0}",
  "command.note.host_only": "/note はホストのみ使用できます。",
  "command.note.cleared": "ノートを削除しました。",
  "command.note.set": "ノートを設定しました：{0}",
  "command.color.invalid": "無効なカラーID。有効範囲：0–17。",
  "command.color.set": "カラーを {0}（{1}）に変更しました。",
  "command.title.too_long": "称号が長すぎます。最大12文字。",
  "command.title.cleared": "称号を削除しました。",
  "command.title.set": "称号を設定しました：{0}",
  "welcome.join": "ようこそ、{0}！フレンドコード：{1} | ルーム：{2}"
}
""";

    private const string DefaultPt = """
{
  "command.unknown": "Comando desconhecido /{0}. Digite /help para ver a lista.",
  "command.error": "Ocorreu um erro ao executar /{0}.",
  "command.usage": "Uso: /{0}",
  "command.help.list": "=== Comandos ===",
  "command.help.entry": "/{0} — {1}",
  "command.help.unknown": "Comando desconhecido: /{0}",
  "command.help.aliases": "Aliases: {0}",
  "command.note.host_only": "Apenas o anfitrião pode usar /note.",
  "command.note.cleared": "Nota removida.",
  "command.note.set": "Nota definida: {0}",
  "command.color.invalid": "ID de cor inválido. Intervalo válido: 0–17.",
  "command.color.set": "Cor alterada para {0} ({1}).",
  "command.title.too_long": "Título muito longo. Máximo 12 caracteres.",
  "command.title.cleared": "Título removido.",
  "command.title.set": "Título definido: {0}",
  "welcome.join": "Bem-vindo, {0}! Código de amigo: {1} | Sala: {2}"
}
""";

    private const string DefaultPtBr = """
{
  "command.unknown": "Comando desconhecido /{0}. Digite /help para ver a lista.",
  "command.error": "Ocorreu um erro ao executar /{0}.",
  "command.usage": "Uso: /{0}",
  "command.help.list": "=== Comandos ===",
  "command.help.entry": "/{0} — {1}",
  "command.help.unknown": "Comando desconhecido: /{0}",
  "command.help.aliases": "Apelidos: {0}",
  "command.note.host_only": "Somente o anfitrião pode usar /note.",
  "command.note.cleared": "Nota removida.",
  "command.note.set": "Nota definida: {0}",
  "command.color.invalid": "ID de cor inválido. Intervalo válido: 0–17.",
  "command.color.set": "Cor alterada para {0} ({1}).",
  "command.title.too_long": "Título muito longo. Máximo 12 caracteres.",
  "command.title.cleared": "Título removido.",
  "command.title.set": "Título definido: {0}",
  "welcome.join": "Bem-vindo, {0}! Código de amigo: {1} | Sala: {2}"
}
""";
}
