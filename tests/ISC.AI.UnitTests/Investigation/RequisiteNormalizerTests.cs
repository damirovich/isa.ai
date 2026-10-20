using ISC.AI.Profile.Investigation.Domain.Services;
using Shouldly;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Нормализация реквизитов для пересечений (ТО-мат-11): разные написания одного госномера, ФИО и адреса дают
/// один ключ, разные значения — разные ключи. Пересечения (ТФ-ПЕР-07) сравнивают ключи точно, поэтому
/// ошибка здесь — это пропущенное или ложное «ПЕРЕСЕЧЕНИЕ!».
/// </summary>
public sealed class RequisiteNormalizerTests
{
    [Theory(DisplayName = "Госномер: регистр, пробелы, дефисы, точки и кириллица-омоглифы дают один ключ")]
    [InlineData("01 KG 123 ABC")]
    [InlineData("01kg123abc")]
    [InlineData("01-KG-123-ABC")]
    [InlineData(" 01 kg 123 авс ")]
    [InlineData("01 KG 123 АВС")]
    public void Plate_variants_share_one_key(string plate) =>
        RequisiteNormalizer.Plate(plate).ShouldBe("01KG123ABC");

    [Fact(DisplayName = "Госномер: разные номера — разные ключи; пустой или из одних знаков — null")]
    public void Plate_distinguishes_and_rejects_empty()
    {
        RequisiteNormalizer.Plate("01 KG 123 ABC").ShouldNotBe(RequisiteNormalizer.Plate("01 KG 124 ABC"));
        RequisiteNormalizer.Plate("B 1234 AB").ShouldBe("B1234AB");
        RequisiteNormalizer.Plate(null).ShouldBeNull();
        RequisiteNormalizer.Plate("   ").ShouldBeNull();
        RequisiteNormalizer.Plate(" - . ").ShouldBeNull();
    }

    [Theory(DisplayName = "ФИО: регистр, «ё/е», порядок слов и знаки препинания не влияют на ключ")]
    [InlineData("Семёнов Пётр Иванович")]
    [InlineData("семенов петр иванович")]
    [InlineData("Иванович Петр Семенов")]
    [InlineData("  СЕМЕНОВ,  Пётр   Иванович. ")]
    public void Name_variants_share_one_key(string name) =>
        RequisiteNormalizer.PersonName(name).ShouldBe("иванович петр семенов");

    [Fact(DisplayName = "ФИО: киргизские буквы сохраняются; дефис склеивает слово; разные люди — разные ключи")]
    public void Name_keeps_kyrgyz_letters_and_joins_hyphens()
    {
        RequisiteNormalizer.PersonName("Үсөнов Ңурбек").ShouldBe("ңурбек үсөнов");
        RequisiteNormalizer.PersonName("Петров-Водкин Кузьма").ShouldBe(RequisiteNormalizer.PersonName("петроВводкин кузьма"));
        RequisiteNormalizer.PersonName("Асанов Бакыт").ShouldNotBe(RequisiteNormalizer.PersonName("Асанова Бакыт"));
        RequisiteNormalizer.PersonName(null).ShouldBeNull();
        RequisiteNormalizer.PersonName(" . , ").ShouldBeNull();
    }

    [Theory(DisplayName = "Адрес: сокращения, метки города и дома, регистр и пунктуация дают один ключ")]
    [InlineData("г. Бишкек, ул. Токтогула, д. 1, кв. 5")]
    [InlineData("Бишкек улица Токтогула 1 квартира 5")]
    [InlineData("город Бишкек, УЛ.Токтогула, дом 1, кв.5")]
    [InlineData("Бишкек шаары, Токтогула көчөсү, 1, батир 5")]
    public void Address_variants_share_one_key(string address)
    {
        var expected = "бишкек ул токтогула 1 кв 5";

        // Киргизская форма ставит тип улицы после названия — порядок слов в адресе сохраняется и значим.
        if (address.Contains("көчөсү", StringComparison.Ordinal))
        {
            expected = "бишкек токтогула ул 1 кв 5";
        }

        RequisiteNormalizer.Address(address).ShouldBe(expected);
    }

    [Fact(DisplayName = "Адрес: дефис в названии снимается, дробный номер «5/1» и тип «мкр» сохраняются, одиночная «/» — не слово")]
    public void Address_handles_hyphens_slashes_and_microdistricts()
    {
        RequisiteNormalizer.Address("г. Кара-Балта").ShouldBe("карабалта");
        RequisiteNormalizer.Address("мкр. Джал, д. 5/1").ShouldBe("мкр джал 5/1");
        RequisiteNormalizer.Address("микрорайон Джал 5/1").ShouldBe("мкр джал 5/1");
        RequisiteNormalizer.Address("ул. Киевская / ул. Токтогула").ShouldBe("ул киевская ул токтогула");
        RequisiteNormalizer.Address("пр. Чуй 10").ShouldNotBe(RequisiteNormalizer.Address("ул. Чуй 10"));
        RequisiteNormalizer.Address("   ").ShouldBeNull();
    }

    [Theory(DisplayName = "ADR-0055: телефон — только цифры; местный номер с нулём или без кода дополняется 996; «00» международного выхода снимается")]
    [InlineData("+996 555 12-34-56")]
    [InlineData("996555123456")]
    [InlineData("0555 123 456")]
    [InlineData("(0555) 12-34-56")]
    [InlineData("555 123 456")]
    [InlineData("00996 555 123 456")]
    public void Phone_variants_share_one_key(string phone) =>
        RequisiteNormalizer.Phone(phone).ShouldBe("996555123456");

    [Fact(DisplayName = "ADR-0055: иностранный номер — цифрами как есть; короткий внутренний — как есть; без цифр — null")]
    public void Phone_keeps_foreign_and_short_numbers()
    {
        RequisiteNormalizer.Phone("+7 912 345-67-89").ShouldBe("79123456789");
        RequisiteNormalizer.Phone("8 (912) 345-67-89").ShouldBe("89123456789"); // не «10 цифр с 0» — местным не считается
        RequisiteNormalizer.Phone("12-34").ShouldBe("1234");
        RequisiteNormalizer.Phone("+996 555 123 456").ShouldNotBe(RequisiteNormalizer.Phone("+996 555 123 457"));
        RequisiteNormalizer.Phone(null).ShouldBeNull();
        RequisiteNormalizer.Phone("   ").ShouldBeNull();
        RequisiteNormalizer.Phone("нет номера").ShouldBeNull();
        RequisiteNormalizer.Phone("+ ( ) -").ShouldBeNull();
    }
}
