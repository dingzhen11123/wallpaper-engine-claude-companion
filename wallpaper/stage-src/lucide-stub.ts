// 替换 Folia 的 services/lucideIconResolver.ts：原文件给 1900 个图标各生成一个按需加载的小文件，
// 只有 AI 主题里的图标才用得到，舞台用默认主题。样式遇到「没有这个图标」都有自己的退路。
export type LucideIconStatus = 'loading' | 'ready' | 'missing';
export const loadLucideIcons = async (_names: readonly unknown[] | undefined): Promise<void> => {};
export const hasLucideIcon = async (_name: string): Promise<boolean> => false;
export const resolveLucideIcon = (_name: string | null | undefined) => null;
export const resolveLucideIconNode = (_name: string | null | undefined) => null;
export const getLucideIconStatus = (_name: string | null | undefined): LucideIconStatus => 'missing';
export const resolveLucideIconNames = (_names: readonly string[] | undefined): string[] => [];
export const subscribeLucideIcons = (_listener: () => void) => () => {};
export const getLucideIconRevision = () => 0;
