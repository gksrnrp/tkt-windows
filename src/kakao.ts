/**
 * 카카오톡 연동 계층.
 *
 * 읽기·전송 모두 직접 만든 네이티브 헬퍼가 담당한다. 카카오톡 UI 를 직접 다루므로 빠르고
 * 카카오톡 창을 앞으로 끌어올리지 않는다.
 *
 * 헬퍼는 플랫폼마다 다르지만 CLI 계약(명령·JSON 출력·오류 문구)은 똑같다.
 *   macOS   → native/tkt-ax       (Accessibility API)
 *   Windows → native/tkt-win.exe  (Win32 + UI Automation)
 *
 * 채팅방은 카카오톡 창 제목으로 식별한다. 닫혀 있는 방은 헬퍼가 채팅 목록에서 찾아
 * 열 수 있다. 외부 도구 없이 이 저장소만으로 동작한다.
 */
import { execFile } from 'node:child_process'
import { existsSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { promisify } from 'node:util'

const run = promisify(execFile)

/** 헬퍼의 read 가 돌려주는 메시지 한 줄. */
export type Message = {
  time: string
  author: string | null
  body: string
  mine: boolean
}

/**
 * 헬퍼는 카카오톡 UI를 직접 다루기 때문에 두 호출이 겹치면 서로를 방해한다.
 * 읽기·전송을 한 줄로 세워 순차 실행한다.
 */
let queue: Promise<unknown> = Promise.resolve()

function serialize<T>(task: () => Promise<T>): Promise<T> {
  const next = queue.then(task, task)
  queue = next.catch(() => undefined)
  return next
}

const IS_WINDOWS = process.platform === 'win32'

/** 헬퍼 파일 이름. 플랫폼마다 구현이 다르므로 이름도 다르다. */
const HELPER_NAME = IS_WINDOWS ? 'tkt-win.exe' : 'tkt-ax'

// src/ 와 dist/ 모두 프로젝트 루트 한 단계 아래라 같은 경로로 해석된다.
const HELPER = fileURLToPath(new URL(`../native/${HELPER_NAME}`, import.meta.url))

/** 사용자에게 보여줄 헬퍼 실행 명령. 셸마다 표기가 달라서 플랫폼별로 갈라 쓴다. */
const HELPER_COMMAND = IS_WINDOWS ? `.\\native\\${HELPER_NAME}` : `./native/${HELPER_NAME}`

/**
 * execFile 의 실패를 헬퍼가 실제로 남긴 말로 바꾼다.
 *
 * execFile 은 오류 메시지를 "Command failed: <긴 명령줄>" 로 시작하고 헬퍼가 stderr 에 쓴
 * 설명은 그 뒤에 붙인다. UI 는 첫 줄만 보여주므로, 그대로 두면 사용자에게 명령줄만 보이고
 * 정작 무엇이 잘못됐는지는 가려진다. 헬퍼는 원인을 한국어로 또박또박 알려주도록 만들어
 * 뒀으니 그 말이 그대로 올라가야 한다.
 */
function helperError(error: unknown): unknown {
  const stderr = ((error as { stderr?: string }).stderr ?? '').trim()
  // 타임아웃으로 죽었으면 stderr 가 비어 있다. 그때는 원본을 그대로 둔다 —
  // missingTools 가 killed 를 보고 "응답하지 않는다" 고 안내한다.
  if (!stderr) return error

  const wrapped = new Error(stderr)
  // 호출부가 원인을 가려내는 데 쓰는 정보는 그대로 남긴다.
  return Object.assign(wrapped, {
    stderr,
    killed: (error as { killed?: boolean }).killed,
    code: (error as { code?: number }).code,
    cause: error,
  })
}

async function helper(args: string[], timeoutMs = 60_000): Promise<string> {
  try {
    const { stdout } = await run(HELPER, args, {
      timeout: timeoutMs,
      maxBuffer: 32 * 1024 * 1024,
      encoding: 'utf8',
    })
    return stdout
  } catch (error) {
    throw helperError(error)
  }
}

/**
 * 지금 열려 있는 카카오톡 채팅창 제목들. 채팅 목록 창은 빠진다.
 *
 * 창 제목만 훑는 가벼운 조회(0.3초)라 직렬 큐를 타지 않는다. 큐에 넣으면 읽기가
 * 진행 중일 때 Tab 을 눌러도 채팅방 목록이 뜨지 않는다.
 */
export async function listOpenChats(): Promise<string[]> {
  const out = await helper(['windows'])
  return (JSON.parse(out).windows ?? []) as string[]
}

/** 헬퍼가 "채팅창이 안 열려 있다"고 알려줄 때의 표식. 두 헬퍼가 같은 문구를 쓴다. */
const WINDOW_CLOSED = '열려 있지 않습니다'

/**
 * 닫혀 있는 채팅방을 연다.
 *
 * 카카오톡 채팅 목록에서 방을 찾아 여는데, 목록 창까지 닫혀 있으면 되살린다.
 * 앱 포커스는 뺏지 않지만 채팅창이 새로 뜨므로 화면에는 나타난다.
 */
export function openChat(chatName: string): Promise<void> {
  return serialize(async () => {
    await helper(['open', chatName])
  })
}

/**
 * 채팅창의 최근 메시지를 읽는다. 창이 닫혀 있으면 한 번 열고 다시 시도한다.
 *
 * `myName` 은 Windows 헬퍼가 "내가 보낸 메시지" 를 가리는 데 쓴다. macOS 헬퍼는 말풍선
 * 위치로 판단하므로 인자를 무시한다 — 넘겨도 해가 없어서 플랫폼 분기 없이 그냥 넘긴다.
 */
export function readMessages(
  chatName: string,
  limit: number,
  myName?: string,
): Promise<Message[]> {
  return serialize(async () => {
    const parse = async () => {
      const args = ['read', chatName, String(limit)]
      if (myName) args.push(myName)
      const out = await helper(args)
      return (JSON.parse(out).messages ?? []) as Message[]
    }

    try {
      return await parse()
    } catch (error) {
      const detail = [
        (error as { stderr?: string }).stderr ?? '',
        error instanceof Error ? error.message : '',
      ].join('\n')

      if (!detail.includes(WINDOW_CLOSED)) throw error

      await helper(['open', chatName])
      return await parse()
    }
  })
}

export function sendMessage(chatName: string, text: string): Promise<void> {
  return serialize(async () => {
    await helper(['send', chatName, text])
  })
}

/**
 * 헬퍼의 check 가 돌려주는 준비 상태.
 *
 * `trusted` 는 플랫폼마다 뜻이 다르다. macOS 에서는 손쉬운 사용 권한, Windows 에서는
 * 카카오톡 창을 조작할 수 있는지(관리자 권한으로 떠 있지 않은지)를 가리킨다.
 * `loggedIn` 은 Windows 헬퍼만 채운다.
 */
type Readiness = {
  trusted: boolean
  kakaoRunning: boolean
  loggedIn?: boolean
  locked?: boolean
}

const ACCESSIBILITY_HELP = [
  '손쉬운 사용(Accessibility) 권한 — 지금 쓰는 터미널 앱에 권한이 없습니다.',
  '시스템 다이얼로그가 떴다면 「시스템 설정 열기」를 누르세요.',
  '뜨지 않았다면 (이미 목록에 있는데 꺼져 있는 경우) 직접 켜야 합니다.',
  '시스템 설정 → 개인정보 보호 및 보안 → 손쉬운 사용에서 이 터미널 앱을 켜세요.',
  '권한은 터미널 앱마다 따로입니다 (Terminal, iTerm, VS Code 내장 터미널…).',
  '켠 뒤에는 터미널을 완전히 종료했다 다시 열어야 반영됩니다.',
].join('\n')

/**
 * Windows 에는 손쉬운 사용 권한 같은 관문이 없다. 대신 권한 수준이 어긋나면 막힌다.
 * 카카오톡이 관리자 권한으로 떠 있으면 일반 권한 프로세스는 그 창에 손댈 수 없다.
 */
const ELEVATION_HELP = [
  '카카오톡 창을 조작할 수 없습니다 — 권한 수준이 어긋났습니다.',
  '카카오톡이 관리자 권한으로 실행 중이면 일반 터미널에서는 창을 다룰 수 없습니다.',
  '카카오톡을 일반 권한으로 다시 실행하거나, 터미널을 관리자 권한으로 여세요.',
].join('\n')

const BUILD_HELP = IS_WINDOWS
  ? `native/tkt-win.exe — \`npm run build:native\` 로 빌드하세요 (.NET Framework 4 필요, 보통 Windows 에 기본 탑재)`
  : 'native/tkt-ax — `npm run build:native` 로 빌드하세요 (Xcode Command Line Tools 필요)'

const KAKAO_HELP = IS_WINDOWS
  ? '카카오톡 PC 앱 — 실행해 로그인하고, 쓰려는 채팅방 창을 열어두세요.'
  : '카카오톡 Mac 앱 — 실행해 로그인하고, 쓰려는 채팅방 창을 열어두세요.'

/**
 * 헬퍼는 있는데 진단이 실패했을 때의 안내.
 *
 * 예전에는 이 경우도 "빌드하세요" 로 뭉뚱그렸는데, 실제로는 헬퍼가 응답하지 않거나
 * 실행 자체가 막힌 경우가 섞여 있어서 엉뚱한 곳을 보게 만들었다. 원인을 그대로 보여준다.
 */
function helperFailedHelp(error: unknown): string {
  const killed = (error as { killed?: boolean }).killed === true
  const stderr = ((error as { stderr?: string }).stderr ?? '').trim()
  const reason = error instanceof Error ? error.message : String(error)

  const lines = killed
    ? [`native/${HELPER_NAME} — 헬퍼가 10초 안에 응답하지 않았습니다.`]
    : [`native/${HELPER_NAME} — 헬퍼를 실행하지 못했습니다.`]

  lines.push(`직접 실행해 무엇이 나오는지 확인하세요: ${HELPER_COMMAND} check`)
  lines.push(`자세한 진단: npm run doctor`)
  if (stderr) lines.push(`헬퍼 출력: ${stderr}`)
  else if (!killed) lines.push(`오류: ${reason}`)
  return lines.join('\n')
}

/**
 * 준비되지 않은 항목을 안내 문구로 돌려준다.
 *
 * macOS 에서는 접근성 권한이 없으면 AX API 가 통째로 막혀서, 나중에 나오는 오류는 원인을
 * 알려주지 않는다. Windows 에서는 카카오톡이 로그인 전이면 창 구조 자체가 다르다.
 * 그래서 시작할 때 헬퍼에게 한 번 물어보고 여기서 걸러낸다.
 */
export async function missingTools(): Promise<string[]> {
  if (!existsSync(HELPER)) return [BUILD_HELP]

  let readiness: Readiness
  try {
    // 진단 한 번에 10초를 넘길 이유가 없다. 넘긴다면 헬퍼가 멈춘 것이니 그렇게 알린다.
    readiness = JSON.parse(await helper(['check'], 10_000)) as Readiness
  } catch (error) {
    return [helperFailedHelp(error)]
  }

  const missing: string[] = []
  if (!readiness.trusted) missing.push(IS_WINDOWS ? ELEVATION_HELP : ACCESSIBILITY_HELP)
  if (!readiness.kakaoRunning) {
    missing.push(KAKAO_HELP)
  } else if (readiness.locked) {
    // 잠금이 걸리면 열어둔 채팅방 창이 통째로 닫힌다. 이걸 짚어주지 않으면 사용자는
    // 자기가 닫지도 않은 창이 왜 사라졌는지 알 수 없다.
    missing.push(
      [
        '카카오톡 잠금 해제 — 카카오톡이 잠금 상태입니다.',
        '잠금이 걸리면 열어둔 채팅방 창이 모두 닫혀서 읽기도 전송도 할 수 없습니다.',
        'tkt 로 주고받는 동안에는 카카오톡을 직접 만지지 않으니, 카카오톡은 이를 자리 비움으로',
        '보고 다시 잠급니다. 계속 쓰려면 카카오톡 설정에서 자동 잠금을 꺼주세요.',
      ].join('\n'),
    )
  } else if (readiness.loggedIn === false) {
    // 로그인 전에는 채팅 목록 창이 아예 없다. 창을 못 찾는다는 오류만 보여주면
    // 사용자는 채팅방을 열어둔 게 잘못됐나 헤매게 된다.
    missing.push('카카오톡 로그인 — 카카오톡이 실행 중이지만 로그인 전입니다. 로그인한 뒤 다시 실행하세요.')
  }
  return missing
}
