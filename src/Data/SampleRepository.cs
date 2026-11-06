using System;
using System.Collections.Generic;
using System.Data;
using Dapper;

namespace Data
{
    public sealed class SampleRepository
    {
        private readonly IDbConnection connection;

        // 연결의 소유권은 호출자에게 있으며, 호출자가 연결을 해제해야 한다.
        public SampleRepository(IDbConnection connection)
        {
            this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        /// <summary>
        /// [fromUtc, toUtc) 범위의 원본 샘플을 수집 시각 순으로 스트리밍한다.
        /// 열거가 끝날 때까지 연결을 유지해야 한다. 중간에 열거를 중단할 때도
        /// 열거자를 해제하여 데이터 리더를 닫아야 한다(foreach 사용 시 자동 해제).
        /// 공백 판정과 구간 계산은 Core가 담당한다.
        /// </summary>
        public IEnumerable<SampleRow> StreamSamples(int equipmentId, DateTime fromUtc, DateTime toUtc)
        {
            var parameters = new DynamicParameters();
            parameters.Add("equipmentId", equipmentId);
            // SQL DATETIME 변환 시 반올림되지 않도록 DATETIME2(3)의 조회 경계 정밀도를 유지한다.
            parameters.Add("fromUtc", fromUtc, DbType.DateTime2);
            parameters.Add("toUtc", toUtc, DbType.DateTime2);
            return connection.Query<SampleRow>(@"
SELECT CollectedAtUtc, StatusCode
FROM dbo.SAMPLE
WHERE EquipmentId = @equipmentId
  AND CollectedAtUtc >= @fromUtc AND CollectedAtUtc < @toUtc
ORDER BY CollectedAtUtc",
                parameters, buffered: false);
        }
    }
}
