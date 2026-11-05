// 替换 Folia 的 utils/lyrics/workerClient.ts：舞台不走 Worker
import { parseLyricsByFormat } from '@folia/utils/lyrics/parserCore';

export const toWorkerLyricProcessingOptions = <T,>(options: T): T => options;
export const initLyricsWorker = (): Worker => { throw new Error('stage: no lyric worker'); };
export const parseLyricsAsync = async (format: any, content: string, translation = '', options: any = {}, romanization = '') =>
    parseLyricsByFormat(format, content, translation, options, romanization);
