import elementCs from 'element-plus/es/locale/lang/cs'
import elementEn from 'element-plus/es/locale/lang/en'

// Element Plus ships its Czech locale with several accessible names and
// prompts still in English ("Close this dialog", "Go to next page").
const czech = {
  ...elementCs,
  el: {
    ...elementCs.el,
    breadcrumb: { label: 'Drobečková navigace' },
    colorpicker: {
      ...elementCs.el.colorpicker,
      defaultLabel: 'výběr barvy',
      alphaLabel: 'zvolit průhlednost',
      hueLabel: 'zvolit odstín',
      svLabel: 'zvolit sytost a jas',
    },
    datepicker: {
      ...elementCs.el.datepicker,
      dateTablePrompt: 'Dny v měsíci vyberete šipkami a klávesou Enter',
      monthTablePrompt: 'Měsíc vyberete šipkami a klávesou Enter',
      yearTablePrompt: 'Rok vyberete šipkami a klávesou Enter',
      selectedDate: 'Vybrané datum',
      weeksFull: {
        sun: 'Neděle', mon: 'Pondělí', tue: 'Úterý', wed: 'Středa', thu: 'Čtvrtek', fri: 'Pátek', sat: 'Sobota',
      },
    },
    inputNumber: { decrease: 'snížit hodnotu', increase: 'zvýšit hodnotu' },
    dropdown: { toggleDropdown: 'Otevřít nebo zavřít nabídku' },
    pagination: {
      ...elementCs.el.pagination,
      page: 'Stránka',
      prev: 'Předchozí stránka',
      next: 'Další stránka',
    },
    dialog: { close: 'Zavřít okno' },
    drawer: { close: 'Zavřít panel' },
    messagebox: { ...elementCs.el.messagebox, close: 'Zavřít okno' },
    slider: { ...elementCs.el.slider, defaultRangeStartLabel: 'počáteční hodnota', defaultRangeEndLabel: 'koncová hodnota' },
    table: {
      ...elementCs.el.table,
      selectAllLabel: 'Vybrat všechny řádky',
      selectRowLabel: 'Vybrat tento řádek',
      expandRowLabel: 'Rozbalit řádek',
      collapseRowLabel: 'Sbalit řádek',
    },
    tag: { close: 'Odebrat štítek' },
    tour: { next: 'Další', previous: 'Předchozí', finish: 'Dokončit', close: 'Zavřít okno' },
    pageHeader: { title: 'Zpět' },
    carousel: { ...elementCs.el.carousel, leftArrow: 'Šipka doleva', rightArrow: 'Šipka doprava' },
  },
}

/** Element Plus locale for the UI language. */
export function elementLocaleFor(locale: string) {
  return locale === 'cs' ? czech : elementEn
}
