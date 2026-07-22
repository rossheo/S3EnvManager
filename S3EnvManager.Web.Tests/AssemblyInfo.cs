using Xunit;

// 모든 테스트가 하나의 실제 인프라(Postgres + 실 AWS)를 공유하고 "role당 활성 CMK 하나" 같은
// 전역 전제가 있어 병렬 실행 시 경쟁 상태가 생긴다 - 직렬 실행 강제.
[assembly: CollectionBehavior(DisableTestParallelization = true)]