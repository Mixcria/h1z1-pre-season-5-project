using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

public sealed partial class VehicleDamageIntegrationTests
{
    [Theory]
    [InlineData("turbo", "compact")]
    [InlineData("turbo", "missing-properties")]
    [InlineData("turbo", "partial-properties")]
    [InlineData("motor", "compact")]
    [InlineData("motor", "missing-properties")]
    [InlineData("motor", "partial-properties")]
    [InlineData("punch", "compact")]
    [InlineData("punch", "missing-properties")]
    [InlineData("punch", "partial-properties")]
    public void EffectsGateway_MalformedAddCannotMutateBeforeACompleteRequest(string lane, string malformed)
    {
        var (service, connection, recorder) = Admit();
        try
        {
            var session = service.ForVehicleTest(connection);
            var car = session.EnterMatchWithCar();
            (uint clientEffect, uint serverEffect) = lane switch
            {
                "motor" => (90001u, 100042u),
                "punch" => (2u, 110755u),
                _ => (90000u, 100023u)
            };
            ulong target = lane == "punch" ? session.Guid : car.Guid;
            if (lane == "motor")
            {
                session.Deliver(Remove(session.Guid, car.Guid, clientEffect, serverEffect));
                Assert.False(car.EngineOn);
            }
            if (lane == "punch") Assert.True(session.Exit());

            object? Animation() => connection.Tag!.GetType().GetProperty("LastPunchAnimation")!
                .GetValue(connection.Tag);
            Assert.Null(Animation());
            byte[] complete = Add(session.Guid, target, clientEffect, serverEffect);
            byte[] bad;
            if (malformed == "compact")
            {
                // Former shortcut: authenticated source and target packed as if this were Remove.
                bad = Remove(session.Guid, target, clientEffect, serverEffect)[..30];
                bad[1] = EffectRequest.AddSub;
            }
            else
            {
                bad = [.. complete, .. new byte[malformed == "partial-properties" ? 19 : 0]];
                bad[70] = 0; // Fifth count is truncated, or every required list is missing.
            }

            bool engine = car.EngineOn;
            int mark = recorder.Sent.Count;
            session.Deliver(bad);
            Assert.Empty(From(recorder, mark));
            Assert.Equal(engine, car.EngineOn);
            Assert.False(session.Boost.IsBoosting(car.Guid));
            Assert.Equal(0ul, car.BoostingCharacterGuid);
            Assert.Equal(0, session.Boost.Presses);
            Assert.Null(Animation());

            session.Deliver(complete);
            if (lane == "turbo")
            {
                Assert.True(session.Boost.IsBoosting(car.Guid));
                Assert.Equal(session.Guid, car.BoostingCharacterGuid);
                Assert.Equal(1, session.Boost.Presses);
            }
            else if (lane == "motor") Assert.True(car.EngineOn);
            else Assert.NotNull(Animation());
        }
        finally { connection.Disconnect(); }
    }

    [Theory]
    [InlineData("turbo", 30)]
    [InlineData("turbo", 53)]
    [InlineData("motor", 30)]
    [InlineData("motor", 53)]
    public void EffectsGateway_TruncatedRemoveCannotReleaseBeforeACompleteRequest(string lane, int length)
    {
        var (service, connection, recorder) = Admit();
        try
        {
            var session = service.ForVehicleTest(connection);
            var car = session.EnterMatchWithCar();
            uint clientEffect = lane == "turbo" ? 90000u : 90001u;
            uint serverEffect = lane == "turbo" ? 100023u : 100042u;
            session.Deliver(Add(session.Guid, car.Guid, clientEffect, serverEffect));
            Assert.True(car.EngineOn);
            if (lane == "turbo") Assert.True(session.Boost.IsBoosting(car.Guid));
            byte[] complete = Remove(session.Guid, car.Guid, clientEffect, serverEffect);
            int mark = recorder.Sent.Count;

            session.Deliver(complete.AsSpan(0, length));
            Assert.Empty(From(recorder, mark));
            Assert.True(car.EngineOn);
            Assert.Equal(0, session.Boost.Releases);
            if (lane == "turbo")
            {
                Assert.True(session.Boost.IsBoosting(car.Guid));
                Assert.Equal(session.Guid, car.BoostingCharacterGuid);
            }

            session.Deliver(complete);
            Assert.Single(From(recorder, mark), packet => packet.Length == 55
                && packet[1] == 0x9e && packet[2] == EffectRequest.RemoveSub);
            if (lane == "turbo")
            {
                Assert.False(session.Boost.IsBoosting(car.Guid));
                Assert.Equal(0ul, car.BoostingCharacterGuid);
                Assert.Equal(1, session.Boost.Releases);
            }
            else Assert.False(car.EngineOn);
        }
        finally { connection.Disconnect(); }
    }
}
