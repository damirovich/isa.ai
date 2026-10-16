namespace ISC.AI.Modules.Chat.UI;

/// <summary>Режим панели чата (ADR-0052): какие из них видны, решает профиль (<c>ChatSettings</c>).</summary>
public enum ChatPanelMode
{
    /// <summary>Вопрос по материалам области — ответ только по найденным фрагментам, со ссылками.</summary>
    Grounded,

    /// <summary>Свободный чат — без поиска и грунтовки.</summary>
    Free,

    /// <summary>Поиск по смыслу — сами фрагменты без модели.</summary>
    Sense,
}
