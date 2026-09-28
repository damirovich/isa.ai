using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Хранилище фигурантов, эталонов и появлений поверх схемы <c>investigation</c> (ТФ-ПЕР-01/02).
/// Чтение — под решёткой: floor ядра и политика профиля применяются к самому фигуранту (у него свои
/// режимные поля, ТБ-070), а сужение по роли/владению — через дело (<see cref="CaseAccessRule"/>):
/// фигурант виден ровно тогда, когда видно его дело. Гриф/подразделение при создании копируются с дела.
/// Появления читаются ПОД СОБСТВЕННЫМ floor'ом (у строки свои гриф/подразделение) поверх видимости фигуранта.
/// Номер «неустановленного лица» выдаётся под advisory-блокировкой дела — гонка двух создателей исключена.
/// </summary>
public sealed class PersonStore(
    IDbContextFactory<InvestigationDbContext> contextFactory,
    IAccessPolicy policy,
    IUserRoleStore roles) : IPersonStore
{
    private const string UnidentifiedPrefix = "Неустановленное лицо № ";
    private const string UniqueViolation = "23505";

    // Пространство ключей advisory-блокировки нумерации неустановленных лиц: двухцелочисленная форма
    // pg_advisory_xact_lock(int, int) не пересекается с одноключевой формой (bigint), которой пользуется
    // хеш-цепочка аудита ядра. Значение произвольное, фиксированное.
    private const int UnidentifiedNumberLockNamespace = 0x4950_534E; // "IPSN" — investigation.person number

    /// <inheritdoc />
    public async Task<IReadOnlyList<PersonRow>> ListByCaseAsync(int caseId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await ProjectRows(AccessiblePersons(db, access, role).Where(p => p.CaseId == caseId), db)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PersonRow?> GetAsync(int personId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await ProjectRows(AccessiblePersons(db, access, role).Where(p => p.Id == personId), db)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<(PersonWriteResult Result, int PersonId)> CreateAsync(PersonDraft draft, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Дело должно быть доступно субъекту; недоступное неотличимо от отсутствующего (ТБ-021).
        var caseFile = await CaseAccessRule.Apply(db.Cases.AsNoTracking(), access, policy, role)
            .FirstOrDefaultAsync(c => c.Id == draft.CaseId, cancellationToken);
        if (caseFile is null)
        {
            return (PersonWriteResult.NotFound, 0);
        }

        // ТФ-ПЕР-06: связь — с фигурантом ЭТОГО дела и типом из справочника. Проверка после доступа к делу:
        // по отказу нельзя узнать ничего о чужих делах (фигурант другого дела неотличим от несуществующего).
        if (!await IsLinkConsistentAsync(db, caseFile.Id, selfId: null, draft, current: null, cancellationToken))
        {
            return (PersonWriteResult.InvalidLink, 0);
        }

        var entity = new Person
        {
            CaseId = caseFile.Id,
            DisplayName = string.Empty,
            IsUnidentified = draft.IsUnidentified,
            Role = draft.Role,
            RoleInCase = Clean(draft.RoleInCase),
            Notes = Clean(draft.Notes),
            // ТБ-070: режимные поля фигуранта — с дела, не из черновика.
            Classification = caseFile.Classification,
            DivisionId = caseFile.DivisionId,
        };
        ApplyQuestionnaire(entity, draft.Questionnaire);
        ApplyLink(entity, draft);

        // Номер и запись — в одной транзакции под блокировкой дела: два одновременных «неустановленных»
        // в одном деле получают разные номера, а не 23505 на уникальном индексе.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (draft.IsUnidentified)
        {
            entity.UnidentifiedNumber = await NextUnidentifiedNumberAsync(db, caseFile.Id, cancellationToken);
        }

        entity.DisplayName = ResolveDisplayName(draft.DisplayName, entity.IsUnidentified, entity.UnidentifiedNumber);
        entity.NameNormalized = NormalizedName(entity);

        db.Persons.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (PersonWriteResult.Ok, entity.Id);
    }

    /// <inheritdoc />
    public async Task<PersonWriteResult> UpdateAsync(
        int personId, PersonDraft edit, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var entity = await AccessiblePersons(db, access, role, tracking: true)
            .FirstOrDefaultAsync(p => p.Id == personId, cancellationToken);
        if (entity is null)
        {
            return PersonWriteResult.NotFound;
        }

        if (!await IsLinkConsistentAsync(db, entity.CaseId, entity.Id, edit, entity, cancellationToken))
        {
            return PersonWriteResult.InvalidLink;
        }

        // Номер неустановленного лица выдаётся один раз и при установлении личности сохраняется:
        // «неустановленное лицо № 3» в материалах дела остаётся ссылкой на этого же человека.
        // Выдача — в транзакции под блокировкой дела (см. NextUnidentifiedNumberAsync).
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (edit.IsUnidentified && entity.UnidentifiedNumber is null)
        {
            entity.UnidentifiedNumber = await NextUnidentifiedNumberAsync(db, entity.CaseId, cancellationToken);
        }

        entity.IsUnidentified = edit.IsUnidentified;
        entity.DisplayName = ResolveDisplayName(edit.DisplayName, edit.IsUnidentified, entity.UnidentifiedNumber);
        entity.Role = edit.Role;
        entity.RoleInCase = Clean(edit.RoleInCase);
        entity.Notes = Clean(edit.Notes);
        ApplyQuestionnaire(entity, edit.Questionnaire);
        ApplyLink(entity, edit);
        entity.NameNormalized = NormalizedName(entity);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PersonWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AppearanceRow>> ListAppearancesAsync(int personId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var visible = AccessiblePersons(db, access, role).Where(p => p.Id == personId);

        // ТБ-020/070: у появления СВОИ гриф/подразделение (с кандидата — носителя другого дела), и они
        // могут быть выше грифа фигуранта. Видимость фигуранта — необходимое, но не достаточное условие:
        // floor ядра и политика профиля применяются к самой строке появления.
        return await db.Appearances.AsNoTracking()
            .Where(BaselineAccess.Filter<Appearance>(access))
            .Where(policy.BuildFilter<Appearance>(access))
            .Where(a => visible.Any(p => p.Id == a.PersonId))
            .OrderByDescending(a => a.ConfirmedAtUtc)
            .ThenByDescending(a => a.Id)
            .Select(a => new AppearanceRow(
                a.Id, a.PersonId, a.CaseId, a.MediaAssetId, a.MediaFaceId, a.FrameIndex, a.FrameTimestampMs,
                a.SearchSessionId, a.CandidateId, a.Similarity, a.Status, a.ConfirmedAtUtc,
                a.ExpertUserId, a.VerifierUserId))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Без решётки по контракту: факт подтверждения уже проверен модулем «Медиа» (правило двух лиц —
    /// <c>TwoPersonRule</c>). Здесь — последний рубеж ТБ-073: эксперт и верификатор обязаны быть
    /// разными людьми, статус — всегда «следственная версия», что бы ни пришло в черновике.
    /// Идемпотентно по кандидату: повторный вызов с тем же <see cref="AppearanceDraft.CandidateId"/>
    /// (повтор после сбоя между фиксацией решения и записью появления, ТФ-ВЕР-03) возвращает идентификатор
    /// уже существующего появления — уникальный индекс <c>candidate_id</c> гарантирует «один кандидат — одно появление».
    /// </remarks>
    public async Task<int> AddAppearanceAsync(AppearanceDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (draft.ExpertUserId == draft.VerifierUserId)
        {
            throw new InvalidOperationException(
                "Одно лицо не может быть экспертом и верификатором одного результата (ТБ-073).");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var entity = new Appearance
        {
            PersonId = draft.PersonId,
            CaseId = draft.CaseId,
            MediaAssetId = draft.MediaAssetId,
            MediaFaceId = draft.MediaFaceId,
            FrameIndex = draft.FrameIndex,
            FrameTimestampMs = draft.FrameTimestampMs,
            SearchSessionId = draft.SearchSessionId,
            CandidateId = draft.CandidateId,
            Similarity = draft.Similarity,
            Status = AppearanceStatus.InvestigativeLead,
            ConfirmedAtUtc = draft.ConfirmedAtUtc,
            ExpertUserId = draft.ExpertUserId,
            VerifierUserId = draft.VerifierUserId,
            Classification = draft.Classification,
            DivisionId = draft.DivisionId,
        };
        db.Appearances.Add(entity);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // Единственный уникальный индекс таблицы — candidate_id: появление для этого кандидата уже есть
            // (параллельный или повторный вызов). Возвращаем существующий, ничего не дублируя.
            db.Entry(entity).State = EntityState.Detached;
            var existingId = await db.Appearances.AsNoTracking()
                .Where(a => a.CandidateId == draft.CandidateId)
                .Select(a => (int?)a.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (existingId is { } id)
            {
                return id;
            }

            throw;
        }

        return entity.Id;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReferencePhotoRow>> ListReferencePhotosAsync(int personId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var visible = AccessiblePersons(db, access, role).Where(p => p.Id == personId);

        return await db.ReferencePhotos.AsNoTracking()
            .Where(r => visible.Any(p => p.Id == r.PersonId))
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Select(r => new ReferencePhotoRow(
                r.Id, r.PersonId, r.MediaAssetId, r.MediaFaceId, r.QualityScore, r.Source, r.LegalBasis,
                r.ReviewDueAt, r.AddedByUserId, r.SupersededById, r.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<(PersonWriteResult Result, int PhotoId)> AddReferencePhotoAsync(
        ReferencePhotoDraft draft, int? supersedesId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var person = await AccessiblePersons(db, access, role)
            .FirstOrDefaultAsync(p => p.Id == draft.PersonId, cancellationToken);
        if (person is null)
        {
            return (PersonWriteResult.NotFound, 0);
        }

        ReferencePhoto? previous = null;
        if (supersedesId is { } oldId)
        {
            previous = await db.ReferencePhotos
                .FirstOrDefaultAsync(r => r.Id == oldId && r.PersonId == person.Id, cancellationToken);
            if (previous is null)
            {
                return (PersonWriteResult.NotFound, 0);
            }
        }

        var photo = new ReferencePhoto
        {
            PersonId = person.Id,
            MediaAssetId = draft.MediaAssetId,
            MediaFaceId = draft.MediaFaceId,
            QualityScore = draft.QualityScore,
            Source = Clean(draft.Source),
            LegalBasis = Clean(draft.LegalBasis),
            ReviewDueAt = draft.ReviewDueAt,
            AddedByUserId = draft.AddedByUserId ?? access.NumericSubjectId,
            // ТБ-070: гриф эталона — гриф фигуранта (= дела), не ниже носителя по построению цепочки.
            Classification = person.Classification,
            DivisionId = person.DivisionId,
        };

        // Смена эталона — одна транзакция: новый записан И прежний помечен заменённым, либо ничего.
        // Прежний НЕ удаляется (ТБ-077) — остаётся в истории с указанием, чем он заменён.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.ReferencePhotos.Add(photo);
        await db.SaveChangesAsync(cancellationToken);

        if (previous is not null)
        {
            previous.SupersededById = photo.Id;
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (PersonWriteResult.Ok, photo.Id);
    }

    /// <summary>Фигуранты под решёткой — общее правило <see cref="PersonAccess"/> (то же у адресов и транспорта).</summary>
    private IQueryable<Person> AccessiblePersons(
        InvestigationDbContext db, AccessContext access, InvestigationRole? role, bool tracking = false) =>
        PersonAccess.Accessible(db, access, policy, role, tracking);

    private static IQueryable<PersonRow> ProjectRows(IQueryable<Person> persons, InvestigationDbContext db) =>
        persons
            .OrderBy(p => p.Id)
            .Select(p => new PersonRow(
                p.Id, p.CaseId, p.DisplayName, p.IsUnidentified, p.UnidentifiedNumber, p.RoleInCase, p.Notes,
                p.Classification, p.DivisionId,
                db.ReferencePhotos.Count(r => r.PersonId == p.Id),
                db.Appearances.Count(a => a.PersonId == p.Id),
                p.Role,
                new PersonQuestionnaire(p.BirthDate, p.BirthYear, p.BirthPlace, p.WorkPlace, p.Residence, p.Sex, p.Alias),
                p.LinkedToPersonId,
                p.LinkTypeId));

    /// <summary>
    /// Согласована ли связь (ТФ-ПЕР-06): поля связи — только у роли «связь»; «чья связь» — другой фигурант
    /// ТОГО ЖЕ дела; «кем приходится» — запись справочника вида «тип связи», действующая либо уже стоящая у
    /// этого фигуранта (<paramref name="current"/>). Дело к этому моменту уже проверено на доступ, поэтому
    /// фигуранты этого дела видны вызывающему — отказ ничего не сообщает о чужих делах.
    /// </summary>
    private static async Task<bool> IsLinkConsistentAsync(
        InvestigationDbContext db, int caseId, int? selfId, PersonDraft draft, Person? current, CancellationToken cancellationToken)
    {
        if (draft.Role != PersonRole.Link)
        {
            return draft.LinkedToPersonId is null && draft.LinkTypeId is null;
        }

        if (draft.LinkedToPersonId is { } targetId
            && (targetId == selfId
                || !await db.Persons.AsNoTracking().AnyAsync(p => p.Id == targetId && p.CaseId == caseId, cancellationToken)))
        {
            return false;
        }

        if (draft.LinkTypeId is { } typeId)
        {
            var item = await db.ReferenceItems.AsNoTracking()
                .Where(i => i.Id == typeId)
                .Select(i => new { i.Kind, i.IsActive })
                .FirstOrDefaultAsync(cancellationToken);
            if (item is null || item.Kind != ReferenceKind.LinkType || (!item.IsActive && current?.LinkTypeId != typeId))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Поля связи — из черновика у роли «связь», у остальных ролей очищаются.</summary>
    private static void ApplyLink(Person entity, PersonDraft draft)
    {
        var isLink = draft.Role == PersonRole.Link;
        entity.LinkedToPersonId = isLink ? draft.LinkedToPersonId : null;
        entity.LinkTypeId = isLink ? draft.LinkTypeId : null;
    }

    /// <summary>
    /// Нормализованное ФИО для пересечений (ТО-мат-11): только у установленного лица — «Неустановленное лицо
    /// № 3» совпадало бы с одноимённой строкой любого другого дела.
    /// </summary>
    private static string? NormalizedName(Person entity) =>
        entity.IsUnidentified ? null : RequisiteNormalizer.PersonName(entity.DisplayName);

    /// <summary>
    /// Переносит анкету (ТФ-ПЕР-05) в сущность целиком; <see langword="null"/> — пустая анкета. При известной
    /// дате рождения год берётся из неё: год и дата не расходятся (то же держит ограничение таблицы).
    /// </summary>
    private static void ApplyQuestionnaire(Person entity, PersonQuestionnaire? questionnaire)
    {
        var q = questionnaire ?? PersonQuestionnaire.Empty;
        entity.BirthDate = q.BirthDate;
        entity.BirthYear = q.BirthDate?.Year ?? q.BirthYear;
        entity.BirthPlace = Clean(q.BirthPlace);
        entity.WorkPlace = Clean(q.WorkPlace);
        entity.Residence = Clean(q.Residence);
        entity.Sex = q.Sex;
        entity.Alias = Clean(q.Alias);
        entity.ResidenceNormalized = RequisiteNormalizer.Address(entity.Residence);
    }

    /// <summary>
    /// Следующий номер неустановленного лица в деле: max + 1 под транзакционной advisory-блокировкой дела.
    /// Вызывать ТОЛЬКО внутри открытой транзакции: блокировка <c>pg_advisory_xact_lock</c> держится до её
    /// конца, поэтому второй создатель в том же деле дождётся фиксации первого и прочитает уже новый max —
    /// уникальный индекс (case_id, unidentified_number) остаётся страховкой, а не рабочим механизмом.
    /// </summary>
    private static async Task<int> NextUnidentifiedNumberAsync(InvestigationDbContext db, int caseId, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Номер неустановленного лица выдаётся только внутри транзакции.");
        }

        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({UnidentifiedNumberLockNamespace}, {caseId})",
            cancellationToken);

        var max = await db.Persons
            .Where(p => p.CaseId == caseId && p.UnidentifiedNumber != null)
            .MaxAsync(p => p.UnidentifiedNumber, cancellationToken);
        return (max ?? 0) + 1;
    }

    private static string ResolveDisplayName(string? displayName, bool isUnidentified, int? unidentifiedNumber)
    {
        var name = Clean(displayName);
        if (name is not null)
        {
            return name;
        }

        if (isUnidentified && unidentifiedNumber is { } number)
        {
            return UnidentifiedPrefix + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        throw new ArgumentException("Установленному фигуранту нужны установочные данные (ФИО).", nameof(displayName));
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<InvestigationRole?> ResolveRoleAsync(AccessContext access, CancellationToken cancellationToken) =>
        access.NumericSubjectId is { } userId
            ? await roles.GetRoleAsync(userId, cancellationToken)
            : null;
}
