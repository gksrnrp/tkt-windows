/**
 * 한글을 조합하는 동안 글자가 입력 자리에 그려지게 만드는 장치. Windows 전용 문제다.
 *
 * IME 는 조합 중인 글자를 "터미널 커서가 있는 칸" 에 그린다. 그런데 Ink 는 프레임을 쓴 뒤
 * 커서를 프레임 맨 아래 다음 줄에 둔다 (프레임 끝에 항상 개행을 붙인다). 그래서 조합 중인
 * 한글이 입력줄이 아니라 그 아래 줄에 나타났다가, 완성되는 순간 입력줄로 뛰어오른다.
 * 영문은 조합 과정이 없어서 이 현상이 없다 — 한글·한자·일본어에서만 보인다.
 *
 * 여기서는 Ink 가 프레임을 쓸 때마다 그 뒤에 "커서를 입력 자리로 옮겨라" 를 덧붙이고,
 * 다음 프레임을 쓰기 직전에 원래 자리로 되돌린다.
 *
 * 되돌리는 것이 핵심이다. Ink 는 이전 화면을 지울 때 커서가 프레임 바로 아래에 있다고
 * 전제하고 "위로 N줄 지우기" 를 보낸다. 옮겨둔 채로 두면 지우는 줄이 한 줄씩 밀려 화면이
 * 부서진다.
 */

/**
 * 입력줄에서 커서가 있어야 할 칸 (1부터 센다). 옮기지 말아야 할 때는 null.
 *
 * `app.tsx` 가 렌더할 때마다 갱신한다. React 상태가 아니라 그냥 값인 이유는, 이 값을 읽는
 * 쪽이 React 바깥(Ink 가 화면에 쓰는 순간)이기 때문이다.
 */
export const imeCursor: { column: number | null } = { column: null }

/** 커서를 한 줄 위 `column` 칸으로. */
function moveToInput(column: number): string {
  return `\u001b[1A\u001b[${column}G`
}

/** 커서를 프레임 바로 아래(Ink 가 있다고 믿는 자리)로. */
const MOVE_BACK = '\u001b[1B\u001b[1G'

export type ImeCursorStdout = {
  /** Ink 의 `render` 에 넘길 출력 스트림. */
  stdout: NodeJS.WriteStream
  /**
   * 앱을 끝낼 때 커서를 제자리로 돌려놓는다.
   * 옮겨둔 채로 끝내면 셸 프롬프트가 입력줄 위에 겹쳐 찍힌다.
   */
  restore(): void
}

export function withImeCursor(stream: NodeJS.WriteStream): ImeCursorStdout {
  // 지금 커서를 입력 자리로 옮겨둔 상태인가.
  let moved = false

  const write = (chunk: unknown, ...rest: unknown[]): boolean => {
    // 문자열이 아닌 출력은 우리가 손댈 것이 없다.
    if (typeof chunk !== 'string') {
      return (stream.write as (...args: unknown[]) => boolean)(chunk, ...rest)
    }

    let payload = ''
    if (moved) {
      payload += MOVE_BACK
      moved = false
    }
    payload += chunk

    // Ink 의 프레임은 개행으로 끝난다. 그 외의 출력(제어문자만 있는 것 등)에는 붙이지 않는다.
    const column = imeCursor.column
    if (column !== null && chunk.endsWith('\n')) {
      const limit = stream.columns ?? 80
      payload += moveToInput(Math.max(1, Math.min(column, limit)))
      moved = true
    }

    return (stream.write as (...args: unknown[]) => boolean)(payload, ...rest)
  }

  const stdout = new Proxy(stream, {
    get(target, property) {
      if (property === 'write') return write
      const value = Reflect.get(target, property, target)
      return typeof value === 'function' ? value.bind(target) : value
    },
  }) as NodeJS.WriteStream

  return {
    stdout,
    restore() {
      if (!moved) return
      stream.write(MOVE_BACK)
      moved = false
    },
  }
}
