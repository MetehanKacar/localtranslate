using System;
using System.Collections.Generic;
using System.Globalization;

namespace LocalTranslate;

/// <summary>
/// UI language (English / Turkish). "auto" follows the Windows display language:
/// Turkish when Windows is Turkish, English otherwise.
/// </summary>
internal static class Loc
{
    public const string Auto = "auto", Turkish = "tr", English = "en";

    /// <summary>The language in use: "tr" or "en".</summary>
    public static string Language { get; private set; } = English;

    public static bool IsTurkish => Language == Turkish;

    public static CultureInfo Culture { get; private set; } = new("en-US");

    /// <summary>Applies the user's setting ("auto", "tr" or "en").</summary>
    public static void Apply(string setting)
    {
        Language = Resolve(setting);
        Culture = new CultureInfo(IsTurkish ? "tr-TR" : "en-US");
    }

    public static string Resolve(string setting) => setting switch
    {
        Turkish => Turkish,
        English => English,
        _ => string.Equals(CultureInfo.InstalledUICulture.TwoLetterISOLanguageName, "tr", StringComparison.OrdinalIgnoreCase)
             ? Turkish : English
    };

    public static string T(string key)
    {
        if (!Strings.TryGetValue(key, out var pair)) return key;
        return IsTurkish ? pair.Tr : pair.En;
    }

    public static string T(string key, params object[] args) => string.Format(Culture, T(key), args);

    /// <summary>Name of a translation language, in the UI language.</summary>
    public static string LanguageName(string code, string? serverEnglishName = null)
    {
        if (IsTurkish && TurkishNames.TryGetValue(code, out var tr)) return tr;
        if (!IsTurkish && !string.IsNullOrWhiteSpace(serverEnglishName)) return serverEnglishName;
        try
        {
            var english = CultureInfo.GetCultureInfo(code).EnglishName;
            if (!string.IsNullOrWhiteSpace(english) && !english.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))
                return english;
        }
        catch (CultureNotFoundException) { }
        return serverEnglishName ?? code;
    }

    // ---------------------------------------------------------------------------------------

    static readonly Dictionary<string, (string En, string Tr)> Strings = new()
    {
        // Tray
        ["tray.enabled"] = ("Translation on", "Çeviri açık"),
        ["tray.screen"] = ("Translate whole screen", "Tüm ekranı çevir"),
        ["tray.region"] = ("Select text from screen (incl. images)", "Ekrandan metin seç (resimler dahil)"),
        ["tray.target"] = ("Target language: {0}", "Hedef dil: {0}"),
        ["tray.hover"] = ("Translate on hover", "Üzerine gelince çevir"),
        ["tray.selection"] = ("Translate on selection", "Seçince çevir"),
        ["tray.settings"] = ("Settings…", "Ayarlar…"),
        ["tray.exit"] = ("Exit", "Çıkış"),
        ["tray.on"] = ("On", "Açık"),
        ["tray.off"] = ("Off", "Kapalı"),
        ["tray.screenOn"] = ("Screen translation on", "Ekran çevirisi açık"),

        // Bubble
        ["bubble.loading"] = ("Translating…", "Çevriliyor…"),
        ["bubble.failed"] = ("Translation failed: {0}", "Çeviri yapılamadı: {0}"),
        ["bubble.sameLanguage"] = ("This text is already in the target language.", "Metin zaten hedef dilde."),
        ["bubble.copy"] = ("Copy", "Kopyala"),
        ["bubble.copied"] = ("Copied", "Kopyalandı"),
        ["bubble.copyFailed"] = ("Copy failed", "Kopyalanamadı"),

        // Toasts and notifications
        ["toast.on"] = ("LocalTranslate: On", "LocalTranslate: Açık"),
        ["toast.off"] = ("LocalTranslate: Off", "LocalTranslate: Kapalı"),
        ["toast.screenOn"] = ("Screen translation: On (→ {0})", "Ekran çevirisi: Açık (→ {0})"),
        ["toast.screenOff"] = ("Screen translation: Off", "Ekran çevirisi: Kapalı"),
        ["problem.screen"] = ("Screen translation", "Ekran çevirisi"),
        ["problem.hotkey"] = ("Couldn't register shortcut", "Kısayol kaydedilemedi"),
        ["problem.hotkeyText"] = ("{0} may be used by another application. You can change it in Settings.", "{0} başka bir uygulama tarafından kullanılıyor olabilir. Ayarlardan değiştirebilirsin."),
        ["problem.server"] = ("Can't reach LibreTranslate", "LibreTranslate'e ulaşılamıyor"),
        ["problem.serverText"] = ("{0} isn't responding. Is Docker Desktop running? In a terminal: docker start libretranslate", "{0} yanıt vermiyor. Docker Desktop açık mı? Terminalde: docker start libretranslate"),
        ["app.running"] = ("LocalTranslate is already running. Its icon is in the notification area next to the clock.", "LocalTranslate zaten çalışıyor. Simgesi saatin yanındaki bildirim alanında."),

        // Translator errors
        ["error.unreachable"] = ("Couldn't reach {0}. Is LibreTranslate running?", "{0} adresine ulaşılamadı. LibreTranslate çalışıyor mu?"),
        ["error.timeout"] = ("LibreTranslate timed out.", "LibreTranslate zaman aşımına uğradı."),
        ["error.unexpected"] = ("Unexpected response (HTTP {0})", "Beklenmeyen yanıt (HTTP {0})"),
        ["error.ocr"] = ("Windows OCR not found. Add a language pack in Settings > Time & language > Language & region.", "Windows OCR bulunamadı. Ayarlar > Zaman ve dil > Dil'den bir dil paketi ekleyin."),

        // Screen text selection
        ["region.reading"] = ("Reading text…", "Metin okunuyor…"),
        ["region.none"] = ("No text found on this screen · Esc: close", "Bu ekranda metin bulunamadı · Esc: kapat"),
        ["region.help"] = ("Drag to select text · Click a word: its sentence · Esc: cancel", "Sürükleyerek metni seç · Bir kelimeye tıkla: cümlesi · Esc: iptal"),
        ["region.noneThere"] = ("No text there. Try again · Esc: cancel", "Orada metin bulunamadı. Tekrar dene · Esc: iptal"),

        // Settings window
        ["set.title"] = ("LocalTranslate Settings", "LocalTranslate Ayarları"),
        ["set.language"] = ("Interface language", "Arayüz dili"),
        ["set.langAuto"] = ("Automatic (Windows language)", "Otomatik (Windows dili)"),
        ["set.target"] = ("Target language", "Hedef dil"),
        ["set.sourceAuto"] = ("The source language is detected automatically.", "Kaynak dil otomatik algılanır."),
        ["set.methods"] = ("Translation methods", "Çeviri yöntemleri"),
        ["set.hover"] = ("Translate the sentence under the mouse", "Fareyle üzerine gelince cümleyi çevir"),
        ["set.delay"] = ("Hover delay", "Bekleme süresi"),
        ["set.selection"] = ("Translate selected text", "Seçince çevir"),
        ["set.fallbacks"] = ("Fallback methods", "Yedek yöntemler"),
        ["set.ocr"] = ("Read text in images and unreadable text from the screen (OCR)", "Resimlerdeki ve okunamayan yazıları ekrandan oku (OCR)"),
        ["set.clipboard"] = ("If a selection can't be read, copy it with Ctrl+C", "Seçim okunamazsa Ctrl+C ile kopyalayarak oku"),
        ["set.clipboardHint"] = ("Your clipboard contents are restored. Not used in terminals, Excel or File Explorer.", "Panodaki içeriğin geri yüklenir. Terminal, Excel ve Dosya Gezgini'nde kullanılmaz."),
        ["set.shortcuts"] = ("Shortcuts", "Kısayollar"),
        ["set.scToggle"] = ("On / off", "Aç / kapat"),
        ["set.scScreen"] = ("Translate whole screen", "Tüm ekranı çevir"),
        ["set.scRegion"] = ("Select text from screen", "Ekrandan metin seç"),
        ["set.scHint"] = ("Click a box and press the new key combination.", "Kutuya tıklayıp yeni kombinasyona bas."),
        ["set.server"] = ("LibreTranslate server", "LibreTranslate sunucusu"),
        ["set.apiKey"] = ("API key (optional)", "API anahtarı (isteğe bağlı)"),
        ["set.startup"] = ("Start with Windows", "Windows açılınca başlat"),
        ["set.cancel"] = ("Cancel", "İptal"),
        ["set.save"] = ("Save", "Kaydet"),
        ["set.checking"] = ("Checking server…", "Sunucu kontrol ediliyor…"),
        ["set.connected"] = ("● Server connected · {0} languages", "● Sunucu bağlı · {0} dil"),
        ["set.unreachable"] = ("● Can't reach the server. Is Docker Desktop running? (docker start libretranslate)", "● Sunucuya ulaşılamıyor. Docker Desktop açık mı? (docker start libretranslate)"),
    };

    static readonly Dictionary<string, string> TurkishNames = new()
    {
        ["en"] = "İngilizce", ["sq"] = "Arnavutça", ["ar"] = "Arapça", ["az"] = "Azerice", ["eu"] = "Baskça",
        ["bn"] = "Bengalce", ["bg"] = "Bulgarca", ["ca"] = "Katalanca", ["zh-Hans"] = "Çince (Basitleştirilmiş)",
        ["zh-Hant"] = "Çince (Geleneksel)", ["cs"] = "Çekçe", ["da"] = "Danca", ["nl"] = "Felemenkçe",
        ["eo"] = "Esperanto", ["et"] = "Estonca", ["fi"] = "Fince", ["fr"] = "Fransızca", ["gl"] = "Galiçyaca",
        ["de"] = "Almanca", ["el"] = "Yunanca", ["he"] = "İbranice", ["hi"] = "Hintçe", ["hu"] = "Macarca",
        ["id"] = "Endonezce", ["ga"] = "İrlandaca", ["it"] = "İtalyanca", ["ja"] = "Japonca", ["ko"] = "Korece",
        ["ky"] = "Kırgızca", ["lv"] = "Letonca", ["lt"] = "Litvanca", ["ms"] = "Malayca", ["nb"] = "Norveççe",
        ["fa"] = "Farsça", ["pl"] = "Lehçe", ["pt"] = "Portekizce", ["pt-BR"] = "Portekizce (Brezilya)",
        ["ro"] = "Romence", ["ru"] = "Rusça", ["sk"] = "Slovakça", ["sl"] = "Slovence", ["es"] = "İspanyolca",
        ["sw"] = "Svahili", ["sv"] = "İsveççe", ["tl"] = "Tagalogca", ["th"] = "Tayca", ["tr"] = "Türkçe",
        ["uk"] = "Ukraynaca", ["ur"] = "Urduca", ["vi"] = "Vietnamca",
    };
}
