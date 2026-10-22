using System;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Media.Application.Features.Settings;

/// <summary>
/// Настройки поиска и верификации для экранов (ADR-0054): ползунок порога у оператора, тексты режима в очереди и окне
/// сличения, экран Администратора.
/// </summary>
/// <param name="Mode">Действующий режим подтверждения.</param>
/// <param name="ThresholdPercent">Рабочий порог, %.</param>
/// <param name="MinThresholdPercent">Нижняя граница порога, %.</param>
/// <param name="UpdatedAtUtc">Когда меняли последний раз; <see langword="null"/> — не меняли.</param>
/// <param name="UpdatedByUserId">Кто менял последним.</param>
/// <param name="CanManage">Текущий субъект вправе менять настройки (экран Администратора).</param>
/// <param name="ScaleDescription">Шкала процентов словами: «балл 0,363 = 70 %».</param>
/// <param name="TranscriptSpeakers">Размечать дикторов при расшифровке загруженных материалов (ADR-0062 v0.3).</param>
public sealed record MediaSettingsView(
    ConfirmationMode Mode,
    int ThresholdPercent,
    int MinThresholdPercent,
    DateTime? UpdatedAtUtc,
    int? UpdatedByUserId,
    bool CanManage,
    string ScaleDescription,
    bool TranscriptSpeakers = true);

/// <summary>
/// Прочитать настройки поиска и верификации (ADR-0054). Значения не секретны — их видит каждый вошедший: оператору
/// нужны границы ползунка, эксперту — режим, в котором он решает. Без субъекта — отказ (fail-closed).
/// </summary>
public sealed record GetMediaSettingsQuery : IRequest<ResponseDto<MediaSettingsView>>
{
    /// <inheritdoc cref="GetMediaSettingsQuery" />
    public sealed class Handler(
        IMediaSettingsStore store, IMediaAdministration administration, ISubjectProvider subjectProvider, MediaSearchOptions options)
        : IRequestHandler<GetMediaSettingsQuery, ResponseDto<MediaSettingsView>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<MediaSettingsView>> Handle(GetMediaSettingsQuery query, CancellationToken cancellationToken)
        {
            if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is null)
            {
                return ResponseDto<MediaSettingsView>.BadRequest("Субъект не установлен.");
            }

            var settings = await store.GetAsync(cancellationToken);
            var canManage = await administration.CanManageSettingsAsync(cancellationToken);
            return ResponseDto<MediaSettingsView>.Ok(new MediaSettingsView(
                settings.Mode, settings.ThresholdPercent, settings.MinThresholdPercent,
                settings.UpdatedAtUtc, settings.UpdatedByUserId, canManage, options.Scale.Describe(), settings.TranscriptSpeakers));
        }
    }
}

/// <summary>
/// Изменить настройки поиска и верификации (ТФ-ВЕР-07, ТФ-ПЛ-08, ТФ-АДМ-03, ADR-0054): режим подтверждения, рабочий
/// порог и нижнюю границу. Только с правом настроек (у «Следствия» — закреплено за Администратором).
/// </summary>
/// <remarks>
/// НЕ <see cref="IAuditableRequest"/>: ТФ-АДМ-03 требует аудита ИЗМЕНЕНИЙ — запись с прежними и новыми значениями
/// сквозное поведение не составит; отказ без права тоже пишется (попытка ослабить правило подтверждения — событие
/// безопасности, ТБ-073). Смена режима ничего не пересчитывает: решённые кандидаты остаются как были, начатые разборы
/// доводятся по правилам своего режима (режим хранится на решении).
/// </remarks>
/// <param name="Mode">Режим подтверждения.</param>
/// <param name="ThresholdPercent">Рабочий порог, % (40–99).</param>
/// <param name="MinThresholdPercent">Нижняя граница, % (40–99, не выше рабочего).</param>
public sealed record SetMediaSettingsCommand(ConfirmationMode Mode, int ThresholdPercent, int MinThresholdPercent)
    : IRequest<ResponseDto<MediaSettings>>
{
    /// <summary>Отказ без права настроек.</summary>
    public const string DeniedMessage = "Настройки поиска и верификации меняет Администратор (право «Настройки поиска и верификации»).";

    /// <inheritdoc cref="SetMediaSettingsCommand" />
    public sealed class Handler(
        IMediaSettingsStore store,
        IMediaAdministration administration,
        ISubjectProvider subjectProvider,
        IAuditWriter auditWriter)
        : IRequestHandler<SetMediaSettingsCommand, ResponseDto<MediaSettings>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<MediaSettings>> Handle(SetMediaSettingsCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var subjectId = await subjectProvider.GetCurrentUserIdAsync(cancellationToken);
            if (subjectId is null)
            {
                return ResponseDto<MediaSettings>.BadRequest("Субъект не установлен.");
            }

            // Право — через нейтральный порт: роли ведёт профиль (ADR-0017); отказ — событие журнала.
            if (!await administration.CanManageSettingsAsync(cancellationToken))
            {
                await WriteAsync(subjectId, $"изменение отклонено: нет права; запрошено: {Describe(command)}", cancellationToken);
                return ResponseDto<MediaSettings>.BadRequest(DeniedMessage);
            }

            var previous = await store.GetAsync(cancellationToken);
            if (await store.SaveAsync(command.Mode, command.ThresholdPercent, command.MinThresholdPercent, subjectId, cancellationToken)
                is { } error)
            {
                return ResponseDto<MediaSettings>.BadRequest(error);
            }

            var current = await store.GetAsync(cancellationToken);
            await WriteAsync(subjectId, Changes(previous, current), cancellationToken);
            return ResponseDto<MediaSettings>.Ok(current, "Настройки сохранены и действуют со следующего поиска и решения.");
        }

        private static string Describe(SetMediaSettingsCommand command) =>
            $"режим подтверждения: {ModeLabel(command.Mode)}; рабочий порог: {command.ThresholdPercent} %; "
            + $"нижняя граница: {command.MinThresholdPercent} %";

        // ТФ-АДМ-03: «было → стало» по каждой настройке, чтобы журнал показывал, что именно поменяли.
        private static string Changes(MediaSettings before, MediaSettings after) =>
            $"режим подтверждения: {ModeLabel(before.Mode)} → {ModeLabel(after.Mode)}; "
            + $"рабочий порог: {before.ThresholdPercent} → {after.ThresholdPercent} %; "
            + $"нижняя граница: {before.MinThresholdPercent} → {after.MinThresholdPercent} %";

        private static string ModeLabel(ConfirmationMode mode) =>
            mode == ConfirmationMode.TwoPerson ? "два лица" : "один оператор";

        private Task WriteAsync(int? subjectId, string payload, CancellationToken cancellationToken) =>
            auditWriter.WriteAsync(
                new AuditEntry(
                    AuditAction.Modify,
                    Classification: 0,
                    subjectId,
                    ObjectRef: "media:settings",
                    PayloadSensitive: payload),
                cancellationToken);
    }
}

/// <summary>
/// Размечать ли дикторов при расшифровке загруженных материалов (ТФ-МЕД-19, ADR-0062 v0.3) — общая настройка
/// Администратора; при «Расшифровать заново» сотрудник выбирает для своей записи сам. Право и журнал — как у настроек
/// поиска и верификации: изменение «было → стало», отказ без права тоже в журнале.
/// </summary>
/// <param name="Enabled">Размечать.</param>
public sealed record SetTranscriptSpeakersCommand(bool Enabled) : IRequest<ResponseDto<MediaSettings>>
{
    /// <inheritdoc cref="SetTranscriptSpeakersCommand" />
    public sealed class Handler(
        IMediaSettingsStore store,
        IMediaAdministration administration,
        ISubjectProvider subjectProvider,
        IAuditWriter auditWriter)
        : IRequestHandler<SetTranscriptSpeakersCommand, ResponseDto<MediaSettings>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<MediaSettings>> Handle(SetTranscriptSpeakersCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var subjectId = await subjectProvider.GetCurrentUserIdAsync(cancellationToken);
            if (subjectId is null)
            {
                return ResponseDto<MediaSettings>.BadRequest("Субъект не установлен.");
            }

            if (!await administration.CanManageSettingsAsync(cancellationToken))
            {
                await WriteAsync(subjectId, $"изменение отклонено: нет права; запрошено: разметка дикторов — {Label(command.Enabled)}", cancellationToken);
                return ResponseDto<MediaSettings>.BadRequest(SetMediaSettingsCommand.DeniedMessage);
            }

            var previous = await store.GetAsync(cancellationToken);
            await store.SaveTranscriptSpeakersAsync(command.Enabled, subjectId, cancellationToken);
            var current = await store.GetAsync(cancellationToken);
            await WriteAsync(
                subjectId,
                $"разметка дикторов при расшифровке загруженных материалов: {Label(previous.TranscriptSpeakers)} → {Label(current.TranscriptSpeakers)}",
                cancellationToken);
            return ResponseDto<MediaSettings>.Ok(current, command.Enabled
                ? "Дикторы будут размечаться при расшифровке загруженных материалов."
                : "Загруженные материалы будут расшифровываться без дикторов.");
        }

        private static string Label(bool enabled) => enabled ? "включена" : "выключена";

        private Task WriteAsync(int? subjectId, string payload, CancellationToken cancellationToken) =>
            auditWriter.WriteAsync(
                new AuditEntry(AuditAction.Modify, Classification: 0, subjectId, ObjectRef: "media:settings", PayloadSensitive: payload),
                cancellationToken);
    }
}

/// <summary>Правила команды настроек: режим известен, пороги 40–99 %, рабочий не ниже границы (тексты — из домена).</summary>
public sealed class SetMediaSettingsValidator : AbstractValidator<SetMediaSettingsCommand>
{
    /// <inheritdoc cref="SetMediaSettingsValidator" />
    public SetMediaSettingsValidator()
    {
        RuleFor(c => c)
            .Must(c => MediaSettings.Validate(c.Mode, c.ThresholdPercent, c.MinThresholdPercent) is null)
            .WithMessage(c => MediaSettings.Validate(c.Mode, c.ThresholdPercent, c.MinThresholdPercent) ?? string.Empty)
            .WithName("Settings");
    }
}
