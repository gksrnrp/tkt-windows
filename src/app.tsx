import React, { useCallback, useEffect, useRef, useState } from 'react'
import { Box, Text, useApp, useInput, useStdout } from 'ink'
import TextInput from 'ink-text-input'
import type { Config } from './config.js'
import { imeCursor } from './ime-cursor.js'
import { listOpenChats, readMessages, sendMessage, type Message } from './kakao.js'

type Row = {
  key: string
  time: string
  author: string
  body: string
  mine: boolean
  pending?: boolean
}

function toRow(m: Message, index: number): Row {
  return {
    key: `${index}:${m.time}|${m.author ?? ''}|${m.body}`,
    time: m.time || '  :  ',
    author: m.mine ? '나' : (m.author ?? '상대'),
    body: m.body,
    mine: m.mine,
  }
}

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms))

/**
 * 터미널에서 글자 하나가 차지하는 칸 수.
 *
 * 한글·한자·이모지는 두 칸을 먹는다. 글자 수로 폭을 재면 한글 대화에서 실제 폭의 절반으로
 * 잡혀, 화면에 들어갈 줄 수를 두 배로 착각하게 된다.
 */
function charWidth(codePoint: number): number {
  const wide =
    (codePoint >= 0x1100 && codePoint <= 0x115f) || // 한글 자모
    (codePoint >= 0x2e80 && codePoint <= 0x303e) || // CJK 부수·기호
    (codePoint >= 0x3041 && codePoint <= 0x33ff) || // 가나·호환 자모
    (codePoint >= 0x3400 && codePoint <= 0x4dbf) ||
    (codePoint >= 0x4e00 && codePoint <= 0x9fff) || // 한자
    (codePoint >= 0xa960 && codePoint <= 0xa97f) ||
    (codePoint >= 0xac00 && codePoint <= 0xd7a3) || // 한글 음절
    (codePoint >= 0xf900 && codePoint <= 0xfaff) ||
    (codePoint >= 0xfe30 && codePoint <= 0xfe6f) ||
    (codePoint >= 0xff00 && codePoint <= 0xff60) || // 전각
    (codePoint >= 0xffe0 && codePoint <= 0xffe6) ||
    (codePoint >= 0x1f300 && codePoint <= 0x1f64f) || // 이모지
    (codePoint >= 0x1f900 && codePoint <= 0x1f9ff)
  return wide ? 2 : 1
}

function displayWidth(text: string): number {
  let width = 0
  for (const char of text) width += charWidth(char.codePointAt(0) ?? 0)
  return width
}

/** 폭이 `width` 칸인 자리에 넣었을 때 차지하는 줄 수. */
function lineCount(text: string, width: number): number {
  if (width <= 0) return 1
  let lines = 0
  for (const segment of text.split('\n')) {
    lines += Math.max(1, Math.ceil(displayWidth(segment) / width))
  }
  return lines
}

/**
 * 칸 수를 정확히 `width` 로 맞춘다. 넘치면 자르고 모자라면 공백을 채운다.
 *
 * `padEnd` 는 글자 수로 세기 때문에 한글 이름이 들어오면 칸이 어긋난다 — 세 글자짜리
 * 이름은 여섯 칸을 먹는데 `padEnd(6)` 은 거기에 공백 세 개를 더 붙인다.
 */
function padDisplay(text: string, width: number): string {
  let out = ''
  let used = 0
  for (const char of text) {
    const size = charWidth(char.codePointAt(0) ?? 0)
    if (used + size > width) break
    out += char
    used += size
  }
  return out + ' '.repeat(Math.max(0, width - used))
}

export default function App({ config }: { config: Config }) {
  const { exit } = useApp()
  const { stdout } = useStdout()

  // 지금 보고 있는 채팅방. 설정값으로 시작하지만 Tab 으로 다른 열린 창으로 옮길 수 있다.
  const [chatName, setChatName] = useState(config.displayName)
  const [openChats, setOpenChats] = useState<string[]>([])
  const [pickerIndex, setPickerIndex] = useState(0)
  const [pickerOpen, setPickerOpen] = useState(false)

  const [rows, setRows] = useState<Row[]>([])
  const [pending, setPending] = useState<Row[]>([])
  const [input, setInput] = useState('')
  const [status, setStatus] = useState('연결 중…')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [arrived, setArrived] = useState(false)

  const lastKey = useRef<string | null>(null)
  // 폴링 루프를 깨워 즉시 한 번 더 읽게 하는 신호.
  const wake = useRef<(() => void) | null>(null)

  const refreshNow = useCallback(() => {
    wake.current?.()
  }, [])

  // 시작할 때, 설정된 방의 창이 닫혀 있으면 지금 열려 있는 창으로 붙는다.
  useEffect(() => {
    let cancelled = false
    listOpenChats()
      .then((chats) => {
        if (cancelled) return
        setOpenChats(chats)
        if (chats.length === 0) return
        if (!chats.some((c) => c.includes(config.displayName))) {
          setChatName(chats[0]!)
        }
      })
      .catch(() => undefined)
    return () => {
      cancelled = true
    }
  }, [config.displayName])

  useEffect(() => {
    // 이 플래그는 반드시 이펙트 안의 지역 변수여야 한다. ref 로 공유하면 방을 바꿀 때
    // 정리 함수가 false 로 내려놓은 값을 곧바로 새 이펙트가 true 로 되돌려서, 이전 루프가
    // 죽지 않고 계속 돈다. 그러면 두 루프가 번갈아 setRows 를 호출해 화면이 두 채팅방을
    // 오간다.
    let active = true

    const loop = async () => {
      let first = true
      while (active) {
        try {
          setStatus(first ? '대화 불러오는 중…' : '새 메시지 확인 중…')
          const messages = await readMessages(chatName, config.historyLimit, config.myName)
          if (!active) return

          const next = messages.map(toRow)
          const tail = next.at(-1)?.key ?? null
          if (!first && tail && tail !== lastKey.current) {
            setArrived(true)
            setTimeout(() => setArrived(false), 4000)
          }
          lastKey.current = tail

          setRows(next)
          // 스냅샷이 갱신됐으니 낙관적으로 띄워둔 줄은 정리한다.
          setPending([])
          setError(null)
          first = false
        } catch (e) {
          if (!active) return
          setError(e instanceof Error ? e.message.split('\n')[0] : String(e))
        }

        setStatus('대기 중')
        // 전송 직후에는 즉시 깨어나 다시 읽는다.
        await Promise.race([
          sleep(config.pollIntervalMs),
          new Promise<void>((resolve) => {
            wake.current = resolve
          }),
        ])
        wake.current = null
      }
    }

    void loop()
    return () => {
      active = false
      wake.current?.()
      // 방을 옮기면 이전 대화는 버린다.
      lastKey.current = null
      setRows([])
      setPending([])
    }
  }, [chatName, config.historyLimit, config.pollIntervalMs, config.myName])

  useInput((_input, key) => {
    if (pickerOpen) {
      if (key.escape) {
        setPickerOpen(false)
        return
      }
      if (key.upArrow) {
        setPickerIndex((i) => Math.max(0, i - 1))
        return
      }
      if (key.downArrow) {
        setPickerIndex((i) => Math.min(openChats.length - 1, i + 1))
        return
      }
      if (key.return) {
        const chosen = openChats[pickerIndex]
        setPickerOpen(false)
        if (chosen && chosen !== chatName) {
          // 이전 방 내용은 여기서 같이 비운다. 이펙트 뒷정리에만 맡기면 방 이름이 먼저
          // 바뀌고 목록이 나중에 지워져, 한 프레임 동안 새 방 이름 아래 이전 대화가 남는다.
          setRows([])
          setPending([])
          lastKey.current = null
          setChatName(chosen)
        }
      }
      return
    }

    if (key.tab) {
      // 목록은 열 때마다 새로 읽는다. 그 사이 창이 열리거나 닫혔을 수 있다.
      listOpenChats()
        .then((chats) => {
          if (chats.length === 0) {
            setError('열려 있는 카카오톡 채팅창이 없습니다')
            return
          }
          setOpenChats(chats)
          setPickerIndex(Math.max(0, chats.indexOf(chatName)))
          setPickerOpen(true)
        })
        .catch(() => setError('채팅창 목록을 읽지 못했습니다'))
      return
    }

    if (key.escape) {
      exit()
    }
  })

  const handleSubmit = async (value: string) => {
    const text = value.trim()
    if (!text || busy) return

    setInput('')
    setBusy(true)
    setStatus('전송 중…')

    const optimistic: Row = {
      key: `pending:${Date.now()}`,
      time: new Date().toTimeString().slice(0, 5),
      author: '나',
      body: text,
      mine: true,
      pending: true,
    }
    setPending((prev) => [...prev, optimistic])

    try {
      // 전송은 chat_id 가 아니라 화면에 보이는 채팅방 이름으로 대상을 찾는다.
      await sendMessage(chatName, text)
      setError(null)
      refreshNow()
    } catch (e) {
      setError(e instanceof Error ? e.message.split('\n')[0] : String(e))
      setPending((prev) => prev.filter((r) => r.key !== optimistic.key))
    } finally {
      setBusy(false)
    }
  }

  const terminalRows = stdout?.rows ?? 24
  const terminalColumns = stdout?.columns ?? 80

  // 화면 전체가 터미널보다 커지면 매 렌더마다 터미널이 스크롤하면서 화면이 위아래로 튄다.
  // 특히 타이핑할 때 한 글자마다 튀어서 못 쓴다. 그래서 높이를 고정하고 한 줄을 남긴다.
  //
  // 아래쪽은 평소엔 입력 한 줄이지만 채팅방 목록을 열면 상자로 바뀐다. 그만큼 대화 상자를
  // 줄여야 한다 — 안 그러면 목록을 열 때마다 화면이 통째로 밀려 올라간다.
  const pickerRows = pickerOpen
    ? Math.min(openChats.length, Math.max(3, terminalRows - 12))
    : 0
  const bottomHeight = pickerOpen ? pickerRows + 3 : 1
  // 머리글 1 + 대화 상자 테두리 2 + 아래쪽 + 안내 1, 그리고 여유 2.
  //
  // 여유를 두 줄 잡는 이유: 입력한 글이 길어져 한 줄 접히는 것까지는 흡수해야 하고,
  // 터미널이 스크롤바 같은 것으로 한 칸을 더 먹는 경우도 있다. 한 줄만 남기면 그런
  // 순간마다 화면이 밀린다.
  const bodyHeight = Math.max(3, terminalRows - 6 - bottomHeight)

  // 열린 채팅방이 많으면 목록만으로 화면을 넘긴다. 고른 항목 둘레만 보여준다.
  const pickerStart = Math.min(
    Math.max(0, pickerIndex - Math.floor(pickerRows / 2)),
    Math.max(0, openChats.length - pickerRows),
  )
  // 테두리 2 + 좌우 여백 2 + 시각 칸 6 + 이름 칸 7, 그리고 오른쪽 여유 1.
  // 폭을 실제보다 넓게 잡으면 접히는 줄 수를 적게 세어 상자가 넘친다. 좁게 잡는 쪽이 안전하다.
  const bodyWidth = Math.max(10, terminalColumns - 18)

  const all = [...rows, ...pending]

  // 메시지 개수가 아니라 화면 줄 수로 잘라야 한다. 여러 줄로 접히는 메시지가 섞이면
  // 개수로 자른 목록이 상자보다 훨씬 길어지고, 그만큼 화면이 밀려 나가며 튄다.
  const shown: Row[] = []
  let usedLines = 0
  for (let i = all.length - 1; i >= 0; i--) {
    const row = all[i]!
    const lines = lineCount(row.body, bodyWidth)
    // 첫 줄(가장 최근 메시지)은 상자보다 길더라도 반드시 보여준다.
    if (shown.length > 0 && usedLines + lines > bodyHeight) break
    shown.unshift(row)
    usedLines += lines
  }

  // 조합 중인 한글이 입력 자리에 그려지도록, 커서를 놓을 칸을 알려준다.
  //
  // 렌더 본문에서 넣는다. Ink 는 커밋 단계에서 화면에 쓰는데 useEffect 는 그보다 늦게 돌아서,
  // 이펙트에 넣으면 한 프레임 뒤처진 자리를 가리킨다 (한 글자씩 밀린다).
  //
  // 칸 번호는 1부터 센다: 왼쪽 여백 1 + "> " 2 + 지금까지 입력한 글자의 폭, 그다음 칸.
  // 채팅방 목록이 떠 있거나 전송 중일 때는 입력줄이 없으니 옮기지 않는다.
  imeCursor.column = pickerOpen || busy ? null : 4 + displayWidth(input)

  return (
    <Box flexDirection="column">
      {/* 머리글·안내·오류는 반드시 한 줄로 자른다. 채팅방 이름이나 오류 문구가 길어
          두 줄로 접히면 그만큼 화면이 터미널을 넘고, 그러면 렌더할 때마다 화면이 튄다. */}
      <Box justifyContent="space-between" paddingX={1}>
        <Text bold color="yellow" wrap="truncate-end">
          {chatName}
        </Text>
        <Text color={arrived ? 'green' : 'gray'} wrap="truncate-end">
          {arrived ? '● 새 메시지' : status}
        </Text>
      </Box>

      <Box
        flexDirection="column"
        justifyContent="flex-end"
        borderStyle="round"
        borderColor="gray"
        paddingX={1}
        height={bodyHeight + 2}
        overflow="hidden"
      >
        {shown.length === 0 ? (
          <Text color="gray">아직 표시할 메시지가 없습니다.</Text>
        ) : (
          shown.map((row) => (
            <Box key={row.key} flexDirection="row">
              <Text color="gray">{padDisplay(row.time, 6)}</Text>
              {/* 이름 칸을 일곱 칸으로 잡아 본문과 한 칸 띄운다. 여섯 칸으로 하면 세 글자
                  한글 이름이 칸을 꽉 채워 본문과 붙어버린다 ("홍길동안녕하세요"). */}
              <Text color={row.mine ? 'cyan' : 'magenta'}>
                {padDisplay(row.author, 7)}
              </Text>
              <Box flexGrow={1}>
                <Text dimColor={row.pending} wrap="wrap">
                  {row.body}
                  {row.pending ? ' …' : ''}
                </Text>
              </Box>
            </Box>
          ))
        )}
      </Box>

      {error ? (
        <Box paddingX={1}>
          <Text color="red" wrap="truncate-end">
            오류: {error}
          </Text>
        </Box>
      ) : (
        <Box paddingX={1}>
          <Text color="gray" wrap="truncate-end">
            Tab 채팅방 전환 · Esc 종료
          </Text>
        </Box>
      )}

      {/* 안내·오류는 입력줄 위에 둔다. 입력줄이 화면의 마지막이어야 한다 —
          Windows 에서 한글을 조합하는 동안 IME 는 조합 중인 글자를 터미널 커서 자리에
          그리는데, Ink 는 프레임을 다 그린 뒤 커서를 마지막 줄 아래에 둔다. 안내줄이
          아래에 있으면 조합 중인 글자가 입력줄에서 두 줄이나 떨어진 곳에 나타났다가
          완성되는 순간 입력줄로 뛰어오른다. */}
      {pickerOpen ? (
        <Box flexDirection="column" borderStyle="round" borderColor="yellow" paddingX={1}>
          <Text bold color="yellow" wrap="truncate-end">
            채팅방 전환 (↑↓ 이동, Enter 선택, Esc 취소)
          </Text>
          {openChats.slice(pickerStart, pickerStart + pickerRows).map((name, offset) => {
            const i = pickerStart + offset
            return (
              <Text
                key={name}
                color={i === pickerIndex ? 'yellow' : undefined}
                wrap="truncate-end"
              >
                {i === pickerIndex ? '\u276f ' : '  '}
                {name}
                {name === chatName ? ' (현재)' : ''}
              </Text>
            )
          })}
        </Box>
      ) : (
        <Box paddingX={1}>
          <Text color={busy ? 'gray' : 'green'}>{busy ? '⋯ ' : '> '}</Text>
          <TextInput
            value={input}
            onChange={setInput}
            onSubmit={handleSubmit}
            placeholder={busy ? '전송 중…' : '메시지를 입력하고 Enter'}
            focus={!pickerOpen}
          />
        </Box>
      )}
    </Box>
  )
}
