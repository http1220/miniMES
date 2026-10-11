using Xunit;
using System;
using Core;

namespace UnitTests
{
    public class CommunicationAlarmDetectorTests
    {
        [Fact]
        public void CheckTimeout_BeforeTimeout_ReturnsNone()
        {
            CommunicationAlarmDetector detector = new CommunicationAlarmDetector();

            detector.OnValidResponse(TimeSpan.FromSeconds(0));

            AlarmEvent result = detector.CheckTimeout(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5));
            Assert.Equal(AlarmEvent.None, result);
        }

        [Fact]
        public void CheckTimeout_AtTimeout_ReturnsRaised()
        {
            CommunicationAlarmDetector detector = new CommunicationAlarmDetector();

            detector.OnValidResponse(TimeSpan.FromSeconds(0));

            AlarmEvent result = detector.CheckTimeout(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            Assert.Equal(AlarmEvent.Raised, result);
        }

        [Fact]
        public void CheckTimeout_AlreadyActive_DoesNotRaiseAgain()
        {
            CommunicationAlarmDetector detector = new CommunicationAlarmDetector();

            detector.OnValidResponse(TimeSpan.FromSeconds(0));

            AlarmEvent result = detector.CheckTimeout(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(5));
            Assert.Equal(AlarmEvent.Raised, result);

            result = detector.CheckTimeout(TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(5));
            Assert.Equal(AlarmEvent.None, result);
        }


        [Fact]
        public void CheckTimeout_NewValidResponse_RestartsTimeout()
        {
            CommunicationAlarmDetector detector = new CommunicationAlarmDetector();

            detector.OnValidResponse(TimeSpan.FromSeconds(0));

            detector.OnValidResponse(TimeSpan.FromSeconds(3));

            AlarmEvent result = detector.CheckTimeout(TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(5));

            Assert.Equal(AlarmEvent.None, result);
        }


        [Fact]
        public void OnValidResponse_ThreeSuccessesWhileActive_ClearsOnThird()
        {
            CommunicationAlarmDetector detector = new CommunicationAlarmDetector();

            detector.OnValidResponse(TimeSpan.FromSeconds(0));

            AlarmEvent result = detector.CheckTimeout(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

            Assert.Equal(AlarmEvent.Raised, result);

           result= detector.OnValidResponse(TimeSpan.FromSeconds(6));

           Assert.Equal(AlarmEvent.None, result);

            result= detector.OnValidResponse(TimeSpan.FromSeconds(7));

            Assert.Equal(AlarmEvent.None, result);

            result = detector.OnValidResponse(TimeSpan.FromSeconds(8));

            Assert.Equal(AlarmEvent.Cleared, result);
            
        }

        [Fact]
        public void OnFailedResponse_DuringRecovery_RestartsSuccessCount()
        {
            CommunicationAlarmDetector detector = new CommunicationAlarmDetector();

            detector.OnValidResponse(TimeSpan.FromSeconds(0));

            AlarmEvent result = detector.CheckTimeout(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

            Assert.Equal(AlarmEvent.Raised, result);


            result = detector.OnValidResponse(TimeSpan.FromSeconds(6));

            Assert.Equal(AlarmEvent.None, result);

            detector.OnFailedResponse();

            result = detector.OnValidResponse(TimeSpan.FromSeconds(7));

            Assert.Equal(AlarmEvent.None, result);

            result = detector.OnValidResponse(TimeSpan.FromSeconds(8));

            Assert.Equal(AlarmEvent.None, result);

            result = detector.OnValidResponse(TimeSpan.FromSeconds(9));

            Assert.Equal(AlarmEvent.Cleared, result);
        }

        [Fact]
        public void OnValidResponse_WhileInactive_DoesNotClear()
        {
                        CommunicationAlarmDetector detector = new CommunicationAlarmDetector();

            for(int i = 0; i < 4; i++)
            {
                AlarmEvent result = detector.OnValidResponse(TimeSpan.FromSeconds(i));
                Assert.Equal(AlarmEvent.None, result);
            }
        }

        [Fact]
        public void OnFailedResponse_PreservesLastValidResponseTime()
        {
            CommunicationAlarmDetector detector = new CommunicationAlarmDetector();

            detector.OnValidResponse(TimeSpan.FromSeconds(0));

            detector.OnFailedResponse();

            AlarmEvent result = detector.CheckTimeout(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            Assert.Equal(AlarmEvent.Raised, result);
        }

        [Fact]
        public void CheckTimeout_AfterRecovery_RaisesAgain()
        {
            //탐지기 객체 생성
            CommunicationAlarmDetector detector = new CommunicationAlarmDetector();

            //탐지기 가동 시작

            //탐지기에 0 유효 결과 전송
            AlarmEvent result = detector.OnValidResponse(TimeSpan.FromSeconds(0));
            Assert.Equal(AlarmEvent.None, result);

            //5로 검증
            result = detector.CheckTimeout(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            Assert.Equal(AlarmEvent.Raised, result);

            result = detector.OnValidResponse(TimeSpan.FromSeconds(6));
            Assert.Equal(AlarmEvent.None, result);

            result = detector.OnValidResponse(TimeSpan.FromSeconds(7));
            Assert.Equal(AlarmEvent.None, result);

            result = detector.OnValidResponse(TimeSpan.FromSeconds(8));
            Assert.Equal(AlarmEvent.Cleared, result);

            result = detector.CheckTimeout(TimeSpan.FromSeconds(13), TimeSpan.FromSeconds(5));
            Assert.Equal(AlarmEvent.Raised, result);
        }
        
    }
}