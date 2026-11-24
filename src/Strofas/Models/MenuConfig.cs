namespace Strofas.Models;

/// <summary>Корневая модель конфигурации меню (menu.json).</summary>
public sealed class MenuConfig
{
    /// <summary>Горячка-переключатель, например "Ctrl+B".</summary>
    public string Hotkey { get; set; } = "Ctrl+B";

    /// <summary>Закрывать меню после выполнения действия.</summary>
    public bool CloseAfterAction { get; set; } = true;

    /// <summary>Размер меню в DIP (логических пикселях WPF).</summary>
    public int MenuSize { get; set; } = 640;

    /// <summary>Максимум пунктов на кольцо (дальше — страницы, колесо мыши). 4..16.</summary>
    public int MaxItemsPerRing { get; set; } = 8;

    /// <summary>"auto" — акцент из темы Windows, либо явный hex вида "#5B9DFF".</summary>
    public string Accent { get; set; } = "auto";

    /// <summary>Опциональный токен GitHub для API (список ассетов в редаторе).</summary>
    public string GithubToken { get; set; } = "";

    public List<MenuItemConfig> Items { get; set; } = new();
}

/// <summary>Пункт меню: либо действие, либо категория с детьми.</summary>
public sealed class MenuItemConfig
{
    public string Label { get; set; } = "";

    /// <summary>Путь к иконке (ico/png/exe). Пусто — буква из названия.</summary>
    public string? Icon { get; set; }

    /// <summary>Вложенные пункты (категория). Если не пусто — пункт считается категорией.</summary>
    public List<MenuItemConfig>? Children { get; set; }

    public ActionConfig? Action { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCategory => Children is { Count: > 0 };
}

/// <summary>Действие пункта.</summary>
public sealed class ActionConfig
{
    /// <summary>launch | stop | toggle</summary>
    public string Type { get; set; } = "launch";

    /// <summary>Путь к exe/файлу/папке/URL. Поддерживает переменные окружения (%APPDATA%).</summary>
    public string? Path { get; set; }

    public string? Args { get; set; }

    public string? WorkingDir { get; set; }

    /// <summary>Имя процесса без .exe — для stop/toggle, если PID не отслеживается.</summary>
    public string? ProcessName { get; set; }

    /// <summary>Запускать скрытым окном.</summary>
    public bool Hidden { get; set; }

    public bool IsStopLike => Type is "stop" or "toggle";

    // ---------- github-приложения (type = "github") ----------

    /// <summary>Репозиторий: "owner/repo" или ссылка https://github.com/owner/repo/releases.</summary>
    public string? Repo { get; set; }

    /// <summary>Имя ассета- exe в релизе (указывается точно; в редакторе — выпадающий список).</summary>
    public string? Asset { get; set; }

    /// <summary>"latest" (по умолчанию) или конкретный тег, например "v1.2.3".</summary>
    public string? Tag { get; set; } = "latest";

    /// <summary>Фоновое авто-обновление при открытии меню (по умолчанию включено).</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Опциональный sha256 ожидаемого ассета — сверяется после скачки.</summary>
    public string? Sha256 { get; set; }

    public bool IsGithub => Type == "github";
}
