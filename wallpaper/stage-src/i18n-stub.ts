// 替换 Folia 的 i18n/config.ts：舞台只用中文，不带另外四种语言的语言包
import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import zhCN from '@folia/i18n/locales/zh-CN.ts';

void i18n.use(initReactI18next).init({
    lng: 'zh-CN',
    fallbackLng: 'zh-CN',
    resources: { 'zh-CN': { translation: zhCN } },
    interpolation: { escapeValue: false },
});

export const flattenLocale = (_obj: Record<string, unknown>, _prefix = ''): Record<string, string> => ({});
export type AppLanguagePreference = 'system' | 'en' | 'zh-CN' | 'zh-TW' | 'in' | 'vi';
export const APP_LANGUAGE_STORAGE_KEY = 'folia_app_language';
export const readStoredAppLanguagePreference = (): AppLanguagePreference => 'zh-CN';
export const applyAppLanguagePreference = async (_preference: AppLanguagePreference): Promise<void> => {};
export default i18n;
