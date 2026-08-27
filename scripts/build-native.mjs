#!/usr/bin/env node
/**
 * 플랫폼에 맞는 네이티브 헬퍼를 빌드한다.
 *
 *   macOS   → swiftc  → native/tkt-ax       (Xcode Command Line Tools 필요)
 *   Windows → csc.exe → native/tkt-win.exe  (.NET Framework 4, Windows 기본 탑재)
 *
 * Windows 쪽은 일부러 .NET Framework 를 쓴다. 윈도우에 처음부터 들어 있어서 사용자가
 * SDK 를 따로 깔 필요가 없고, UI Automation 어셈블리도 GAC 에 이미 있다.
 *
 * 소스가 산출물보다 새것일 때만 다시 빌드한다. `npm run dev` 가 매번 이 스크립트를
 * 거치므로, 안 그러면 실행할 때마다 컴파일을 기다려야 한다.
 */
import { execFileSync } from 'node:child_process'
import { existsSync, readdirSync, statSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = join(dirname(fileURLToPath(import.meta.url)), '..')

/** 산출물이 소스보다 최신인가. */
function upToDate(source, output) {
  if (!existsSync(output)) return false
  return statSync(output).mtimeMs >= statSync(source).mtimeMs
}

function die(lines) {
  for (const line of [].concat(lines)) console.error(line)
  process.exit(1)
}

// MARK: - macOS

function buildSwift() {
  const source = join(root, 'native', 'tkt-ax.swift')
  const output = join(root, 'native', 'tkt-ax')
  if (upToDate(source, output)) return output

  try {
    execFileSync('swiftc', ['-O', source, '-o', output], { stdio: 'inherit' })
  } catch {
    die([
      'native/tkt-ax 빌드에 실패했습니다.',
      'Xcode Command Line Tools 가 필요합니다: xcode-select --install',
    ])
  }
  return output
}

// MARK: - Windows

const systemRoot = process.env.SystemRoot ?? 'C:\\Windows'

/**
 * .NET Framework 의 C# 컴파일러를 찾는다.
 *
 * 64비트를 먼저 본다. 32비트로 빌드해도 동작은 하지만, 64비트 카카오톡의 창 핸들을
 * 다룰 때 굳이 비트를 섞을 이유가 없다.
 */
function findCsc() {
  const bases = [
    join(systemRoot, 'Microsoft.NET', 'Framework64'),
    join(systemRoot, 'Microsoft.NET', 'Framework'),
  ]
  for (const base of bases) {
    if (!existsSync(base)) continue
    const versions = readdirSync(base)
      .filter((name) => name.startsWith('v4.'))
      .sort()
      .reverse()
    for (const version of versions) {
      const csc = join(base, version, 'csc.exe')
      if (existsSync(csc)) return csc
    }
  }
  return null
}

/**
 * GAC 에 설치된 어셈블리의 실제 경로.
 *
 * csc.exe 는 기본 검색 경로에 GAC 를 넣지 않아서 /r: 에 전체 경로를 줘야 한다.
 * 폴더 이름에 버전과 공개 키 토큰이 박혀 있으므로 훑어서 찾는다.
 */
function gacAssembly(name) {
  const base = join(systemRoot, 'Microsoft.NET', 'assembly', 'GAC_MSIL', name)
  if (!existsSync(base)) return null
  const versions = readdirSync(base).filter((dir) => dir.startsWith('v4.')).sort().reverse()
  for (const version of versions) {
    const dll = join(base, version, `${name}.dll`)
    if (existsSync(dll)) return dll
  }
  return null
}

function buildCSharp() {
  const source = join(root, 'native', 'tkt-win.cs')
  const output = join(root, 'native', 'tkt-win.exe')
  if (upToDate(source, output)) return output

  const csc = findCsc()
  if (!csc) {
    die([
      'C# 컴파일러(csc.exe)를 찾지 못했습니다.',
      '.NET Framework 4.x 가 필요합니다 — 보통 Windows 에 기본으로 들어 있습니다.',
      `찾아본 곳: ${join(systemRoot, 'Microsoft.NET', 'Framework64')}`,
    ])
  }

  // UI Automation 은 대화 내용을 읽는 유일한 통로다. 없으면 빌드할 이유가 없다.
  const references = ['UIAutomationClient', 'UIAutomationTypes', 'WindowsBase'].map((name) => {
    const dll = gacAssembly(name)
    if (!dll) {
      die([
        `${name}.dll 을 찾지 못했습니다.`,
        'UI Automation 어셈블리가 없으면 대화 내용을 읽을 수 없습니다.',
        '.NET Framework 4.x 가 온전히 설치돼 있는지 확인하세요.',
      ])
    }
    return `/r:${dll}`
  })

  try {
    execFileSync(
      csc,
      [
        '/nologo',
        '/optimize+',
        '/target:exe',
        // 소스가 UTF-8 인데 BOM 이 없다. 이걸 빼면 csc 가 시스템 코드페이지로 읽어
        // 한글 주석과 문자열이 전부 깨진다.
        '/codepage:65001',
        `/out:${output}`,
        ...references,
        source,
      ],
      { stdio: 'inherit' },
    )
  } catch {
    die('native/tkt-win.exe 빌드에 실패했습니다. 위 컴파일 오류를 확인하세요.')
  }
  return output
}

// MARK: - 진입점

if (process.platform === 'darwin') {
  buildSwift()
} else if (process.platform === 'win32') {
  buildCSharp()
} else {
  die([
    `tkt 는 macOS 와 Windows 에서만 동작합니다 (지금 플랫폼: ${process.platform}).`,
    '카카오톡 데스크톱 앱의 UI 를 직접 다루는 도구라 앱이 있는 곳에서만 쓸 수 있습니다.',
  ])
}
