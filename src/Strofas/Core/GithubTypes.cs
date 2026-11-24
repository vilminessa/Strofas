namespace Strofas.Core;

/// <summary>Какая зона лепестка нажата (лепесток с обновлением делится на две).</summary>
internal enum PressZone
{
    Launch,
    Update,
}

/// <summary>
/// Визуальное состояние пункта-приложения из GitHub-релизов.
/// None = всё свеже/нет данных; NotInstalled = зона не рисуется, только статус в хабе.
/// </summary>
internal enum UpdateVisual
{
    None,
    NotInstalled,
    Available,
    Downloading,
    Staged,
    Error,
}
