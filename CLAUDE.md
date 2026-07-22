## C# 코딩 관례의 예외적 사항
- Byte -> byte
- short -> Int16
- int -> Int32
- long -> Int64
- ushort -> UInt16
- uint -> UInt32
- ulong -> UInt64
- String -> string

## 코드 정리
- using 지시문은 필요한 네임스페이스만 포함하도록 정리한다.
- 코드 내에서 사용하지 않는 using 지시문은 제거한다.
- Trailing whitespace는 제거한다.
- 110자 이상의 줄은 적절히 줄바꿈하여 가독성을 높인다.
- 띄어쓰기는 Tab으로 통일한다.
- C# 코딩 관례의 예외적 사항 적용

## 코드 위험 패턴 감지
아래 패턴을 코드에서 발견하면 반드시 경고한다.

- **`.Result` / `.Wait()` 동기 블로킹**: ASP.NET Core에서 데드락 위험.
  `await`를 사용한다.
- **`async void`**: 예외가 캐치되지 않아 프로세스 크래시. 이벤트 핸들러 외 사용 금지.
  `async Task`로 변경한다.
- **`DateTime.Now`**: 서버 로컬 타임존에 종속. `DateTime.UtcNow`를 사용한다.
- **`HttpClient` 직접 생성**: 소켓 고갈(socket exhaustion) 위험.
  `IHttpClientFactory` 또는 DI 주입을 사용한다.
- **`Thread.Sleep` in async context**: 스레드 풀 점유. `await Task.Delay()`로 대체한다.

## git commit 메시지 작성
- 맨 윗줄에 요약 한줄을 작성한다.
- 두번째 줄은 공백 처리한다.
- 세번째 줄부터 상세 항목을 작성한다.