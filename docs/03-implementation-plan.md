# Phase 1~3 구현 계획

## 범위

보완 설계와 [OpenAI API 사용 확정](04-openai-api.md)을 적용하여 C#/.NET 10으로 Core, Daily Dream, Weekly Meditation을 구현한다. UI/inspector와 benchmark는 이번 범위에 포함하지 않는다. 현재 폴더는 Git 저장소가 아니며, 요청되지 않은 commit/push는 하지 않는다.

## 구성과 진행

1. Core의 도메인 계약, SQLite 저장소, immutable Source archive와 invariant 검증을 만든다.
2. OpenAI Responses structured outputs / embeddings 연동을 별도 모듈로 위임 구현한다.
3. 검색·remember·recall·trace를 연결한다. 내부 consolidation/scheduler를 별도 소유 파일로 위임 구현한다.
4. 공식 MCP C# SDK의 로컬 HTTP 서버에 공개 도구 세 개만 연결한다.
5. 주 에이전트가 통합 검토하고 실제 SQLite + fake cognition 테스트, HTTP 계약 테스트 및 build로 검증한다.

## 계획 검토에 반영한 사항

- Revision 외에 Memory, relation, recall의 sequence high-water mark를 run 시작에 저장한다. 실행 중 새로 생긴 데이터가 frozen graph에 들어오지 않도록 한다.
- Source archive와 DB 사이의 장애에 대비하여 ingestion 상태와 원문 파일을 보존하고 같은 Source로 재시도한다.
- 작업별 LLM proposal을 저장한 뒤 적용하고 run/work/proposal 번호로 재시도 중복을 방지한다. 다른 run의 의미상 중복은 제거하지 않는다.
- 주간 비용은 API 호출 전 보수적으로 예약하고 응답 usage로 정산한다. 완료 여부가 불명확한 호출의 예약은 유지한다.
- 저장된 scheduler 기간과 작업 상태로 재시작 후 미완료 작업을 계속한다.
- 원문 하나의 영구적인 추출 실패가 나머지 원문과 스케줄러를 막지 않도록 개별 실패를 보존·보고하고 다음 작업을 진행한다.
- Embedding은 모델·dimension별로 분리하고 기존 기억을 재색인할 내부 실행 경로를 제공한다.

## 구현 기본값과 운영 선택

아래는 변경 가능한 구현 기본값이며, Memory의 의미에 관한 새 invariant가 아니다.

- 입력 상한 4,000 문자, 한 입력의 observation 상한 1개. 초과 raw는 분할을 요청하는 입력 오류로 반환한다. 입력을 잘라 저장하지 않는다.
- Budget N은 설정값이다. `MeditationBudgetUsd`가 없으면 주간 실행을 보류하며 데이터를 소비하거나 주간 완료 상태를 진행하지 않는다.
- API 기본값은 추가 확정 문서를 따른다. Remember는 gpt-5.6-terra / low, Recall은 gpt-5.6-terra / medium, Dream은 gpt-5.6-terra / high, Meditation은 gpt-5.6-sol / high, Embedding은 text-embedding-3-large이다. 값은 역할별 설정으로 변경할 수 있으며 모델 이름은 corpus invariant가 아니다.
- 모든 semantic processing은 OpenAI API를 직접 호출한다. 인증은 OPENAI_API_KEY 환경변수만 사용하고 key를 설정 파일에 저장하지 않는다. 테스트의 canned response/fake cognition은 유료 API 없이 동작을 검증하기 위한 것이며 운영 provider가 아니다.
- 기본 timezone은 Asia/Seoul이다. 완료된 날짜와 주간 구간을 `[start, end)`로 처리한다. 서버가 켜져 있을 때 내부 scheduler가 동작한다.
- 하나의 corpus는 한 서버 프로세스가 소유하며 여러 MCP 클라이언트가 같은 서버를 공유한다. 서버는 기본적으로 loopback에서만 접속한다.
- Relation 종류별 단방향 기록을 유지하므로 같은 방향에 positive와 negative가 별도 기록으로 존재할 수 있다. 기존 relation의 시각은 재발견으로 갱신하지 않는다.

## 검증 초점

원문 보존, 반복/동시 입력 차단, 실패 후 재시도, strict layering, unique Source root 합집합, 부모 불변성, generation barrier, relation 방향·시각, recall 비강화, 주간 변경점 누락 방지, 비용 한도 및 스케줄 재시작을 테스트한다. 실제 OpenAI 계정으로 유료 검증하는 것과 fake provider 검증은 구분하여 보고한다.

Phase 1~3의 구현은 `src/LongJourney.Core`, `src/LongJourney.OpenAI`, `src/LongJourney.Server`에 나뉘어 있다. `tests/LongJourney.Tests`는 실제 SQLite, 가짜 API 응답, 실행 중인 MCP HTTP 서버를 사용해 기능과 경계를 검증한다. 실행·설정 방법은 [운영 안내](05-operations.md)를 참고한다.

## 참고한 공식 자료

- [OpenAI Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs)
- [OpenAI Embeddings](https://developers.openai.com/api/docs/guides/embeddings)
- [OpenAI API pricing](https://developers.openai.com/api/docs/pricing): 예시 토큰 단가는 2026-09-04 확인. 운영 설정으로 관리한다.
- [MCP C# SDK](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/getting-started.html)
