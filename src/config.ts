import { fileURLToPath } from 'node:url'
import { readFile, writeFile } from 'node:fs/promises'

export type Config = {
  /** 카카오톡 채팅창 제목. 채팅방을 식별하는 유일한 키다. */
  displayName: string
  /** 한 번 읽고 나서 다음 읽기까지 쉬는 시간(ms). */
  pollIntervalMs: number
  /** 화면에 유지할 최근 메시지 수. */
  historyLimit: number
  /**
   * 카카오톡에 표시되는 내 이름. Windows 에서만 쓴다.
   *
   * macOS 는 말풍선이 화면 오른쪽에 붙었는지로 내 메시지를 가리지만, Windows 는 앱이
   * 복사해준 텍스트만 볼 수 있어서 위치 정보가 없다. 복사본에는 내 메시지도 내 이름으로
   * 적히므로, 이름을 알아야 "나" 와 "상대" 를 나눌 수 있다.
   */
  myName?: string
}

// 프로젝트 루트의 config.json. src/ 와 dist/ 모두 루트 한 단계 아래라 같은 곳을 가리킨다.
const FILE = fileURLToPath(new URL('../config.json', import.meta.url))

export const DEFAULTS = {
  /**
   * Windows 는 읽기 한 번에 카카오톡 대화 목록을 통째로 복사한다 (접근성 API 로는
   * 대화를 읽을 수 없어서 이 길밖에 없다). 그 왕복이 1초 가까이 걸리고 매번 사용자
   * 클립보드를 잠깐 빌리므로, macOS 보다 여유를 둔다.
   */
  pollIntervalMs: process.platform === 'win32' ? 3_000 : 1_000,
  historyLimit: 30,
}

export async function loadConfig(): Promise<Config | null> {
  try {
    const raw = await readFile(FILE, 'utf8')
    const parsed = JSON.parse(raw) as Partial<Config>
    if (!parsed.displayName) return null
    return {
      displayName: parsed.displayName,
      pollIntervalMs: parsed.pollIntervalMs ?? DEFAULTS.pollIntervalMs,
      historyLimit: parsed.historyLimit ?? DEFAULTS.historyLimit,
      myName: parsed.myName,
    }
  } catch {
    return null
  }
}

export async function saveConfig(config: Config): Promise<string> {
  await writeFile(FILE, JSON.stringify(config, null, 2) + '\n', 'utf8')
  return FILE
}

export const configPath = FILE
