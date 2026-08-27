#!/usr/bin/env node
/**
 * tkt 가 시작되지 않을 때 어느 단계에서 막히는지 찾는다.
 *
 * 각 단계는 실행하기 전에 먼저 제목을 찍는다. 화면에 마지막으로 남은 제목이
 * 곧 막힌 지점이다 — 멈춘 채로 두고 그 제목을 보면 된다.
 */
import { execFileSync } from 'node:child_process'
import { existsSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = join(dirname(fileURLToPath(import.meta.url)), '..')
const isWindows = process.platform === 'win32'
const helper = join(root, 'native', isWindows ? 'tkt-win.exe' : 'tkt-ax')

function step(title) {
  process.stdout.write(`\n\x1b[1m== ${title}\x1b[0m\n`)
}

/** 명령을 돌리고 출력을 그대로 흘린다. 실패하면 false. */
function attempt(command, args, options = {}) {
  try {
    execFileSync(command, args, { stdio: 'inherit', cwd: root, ...options })
    return true
  } catch (error) {
    console.error(`→ 실패: ${error instanceof Error ? error.message : error}`)
    return false
  }
}

if (!isWindows && process.platform !== 'darwin') {
  console.error(`tkt 는 macOS 와 Windows 에서만 동작합니다 (지금 플랫폼: ${process.platform}).`)
  process.exit(1)
}

step('운영체제')
if (isWindows) {
  attempt('cmd', ['/c', 'ver'])
} else {
  attempt('sw_vers', ['-productVersion'])
}

if (isWindows) {
  step('C# 컴파일러 (.NET Framework — 보통 Windows 에 기본 탑재)')
  const systemRoot = process.env.SystemRoot ?? 'C:\\Windows'
  const csc = join(systemRoot, 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe')
  if (existsSync(csc)) {
    console.log(csc)
  } else {
    console.error(`→ ${csc} 이 없습니다. 빌드 스크립트가 다른 버전을 찾아볼 수 있으니 계속 진행합니다.`)
  }

  step('카카오톡 PC 앱 설치 여부')
  const candidates = [
    'C:\\Program Files\\Kakao\\KakaoTalk\\KakaoTalk.exe',
    'C:\\Program Files (x86)\\Kakao\\KakaoTalk\\KakaoTalk.exe',
  ]
  const installed = candidates.find((path) => existsSync(path))
  console.log(installed ?? '→ 기본 설치 경로에서 찾지 못했습니다 (다른 경로에 깔았다면 무시해도 됩니다).')
} else {
  step('Xcode 명령줄 도구 (없으면 여기서 설치 다이얼로그를 띄우고 멈춘다)')
  if (!attempt('xcode-select', ['-p'])) {
    console.error('→ 명령줄 도구가 없습니다. xcode-select --install 로 설치한 뒤 다시 실행하세요.')
    process.exit(1)
  }
}

step('네이티브 헬퍼 빌드')
if (!attempt(process.execPath, [join(root, 'scripts', 'build-native.mjs')])) process.exit(1)
console.log(helper)

step('헬퍼 실행 — 카카오톡 실행·로그인 여부')
if (!attempt(helper, ['check'])) process.exit(1)
if (isWindows) {
  console.log('→ trusted:false 면 카카오톡이 관리자 권한으로 떠 있는 것입니다.')
  console.log('  loggedIn:false 면 카카오톡에 아직 로그인하지 않은 상태입니다.')
} else {
  console.log('→ trusted:false 면 시스템 설정 → 개인정보 보호 및 보안 → 손쉬운 사용에서')
  console.log('  이 터미널 앱을 켜고, 터미널을 완전히 종료했다 다시 여세요.')
}

step('열려 있는 채팅창')
attempt(helper, ['windows'])
console.log('→ 빈 목록이면 카카오톡에서 채팅방 창을 먼저 열어주세요.')

if (isWindows) {
  step('대화 읽기 (클립보드 경로)')
  console.log('카카오톡 PC 는 대화 내용을 접근성 API 에 전혀 내놓지 않습니다. 그래서 읽기는')
  console.log('대화 목록에 Ctrl+A → Ctrl+C 를 보내 복사한 결과를 씁니다 (클립보드는 원래대로')
  console.log('되돌립니다). 위 목록의 채팅방 이름으로 확인하세요:')
  console.log(`  .\\native\\tkt-win.exe read "<채팅방 이름>" 5 "<내 이름>"`)
  console.log('빈 목록이 나오면 복사 형식이 바뀐 것이니 원문을 봅니다:')
  console.log(`  .\\native\\tkt-win.exe raw "<채팅방 이름>"`)
  console.log('창을 아예 못 찾으면 클래스 이름이 바뀐 것이니 트리를 봅니다:')
  console.log(`  .\\native\\tkt-win.exe probe "<채팅방 이름>"`)
}

step('Node 와 tsx (여기서 멈추면 의존성 설치가 덜 끝난 것이다)')
console.log(process.version)
const tsx = join(root, 'node_modules', '.bin', isWindows ? 'tsx.cmd' : 'tsx')
if (existsSync(tsx)) console.log(tsx)
else console.error('→ tsx 가 없습니다. npm install 을 먼저 실행하세요.')

process.stdout.write('\n\x1b[1m모든 단계 통과. npm run dev 를 다시 실행해 보세요.\x1b[0m\n')
