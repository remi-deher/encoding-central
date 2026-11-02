using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Nodes;

namespace Encoding.Runtime.Tests;

internal static partial class Harness
{
    internal sealed partial class Fixture
    {
        public async Task NodeScheduleAsync()
        {
            var library=Guid.NewGuid(); var id=Guid.NewGuid(); var flow=RelayFlow();
            await using(var command=db.CreateCommand("""
                INSERT INTO libraries(id,name) VALUES(@library,@name);
                INSERT INTO library_node_accesses(library_id,node_id,mode,local_path,can_read,can_write) VALUES(@library,@a,'direct',@root,true,true);
                """))
            {
                command.Parameters.AddWithValue("library",library); command.Parameters.AddWithValue("name",library.ToString());
                command.Parameters.AddWithValue("root",directory); command.Parameters.AddWithValue("a",a); await command.ExecuteNonQueryAsync();
            }
            Check(await runtime.EnqueueAsync(new(id,library,"scheduled.mkv",flow.Id,flow.Version),flow,CancellationToken.None),"Enqueue scheduled fixture.");
            async Task Schedule(bool enabled,string slots)
            {
                await using var save=db.CreateCommand("INSERT INTO node_processing_settings(node_id,schedule_enabled,schedule_timezone,schedule_slots) VALUES(@a,@enabled,'UTC',@slots) ON CONFLICT(node_id) DO UPDATE SET schedule_enabled=@enabled,schedule_timezone='UTC',schedule_slots=@slots");
                save.Parameters.AddWithValue("a",a); save.Parameters.AddWithValue("enabled",enabled); save.Parameters.AddWithValue("slots",slots); await save.ExecuteNonQueryAsync();
            }
            await Schedule(true,new string('0',NodeSchedule.SlotCount));
            Check(await runtime.ClaimAsync(a,CancellationToken.None) is null,"A closed node calendar blocks new claims.");
            var now=DateTimeOffset.UtcNow; var open=new char[NodeSchedule.SlotCount]; Array.Fill(open,'0');
            open[(int)now.DayOfWeek*NodeSchedule.SlotsPerDay+now.Hour*NodeSchedule.SlotsPerHour+now.Minute/15]='1';
            await Schedule(true,new string(open));
            var lease=await runtime.ClaimAsync(a,CancellationToken.None);
            Check(lease is not null,"A calendar slot covering the current time allows the claim.");
            Check(await runtime.RenewAsync(lease!.ExecutionId,a,lease.Epoch,CancellationToken.None) is not null,"A calendar never revokes an active lease.");
            await Schedule(true,new string('0',NodeSchedule.SlotCount));
            Check(await runtime.RenewAsync(lease.ExecutionId,a,lease.Epoch,CancellationToken.None) is not null,"Closing the calendar does not interrupt running work.");
            await Schedule(false,new string('0',NodeSchedule.SlotCount));
            await using(var clean=db.CreateCommand("UPDATE executions SET state='cancelled' WHERE id=@id; UPDATE execution_work SET lease_until=NULL WHERE execution_id=@id; DELETE FROM node_processing_settings WHERE node_id=@a;"))
            {
                clean.Parameters.AddWithValue("id", lease.ExecutionId);
                clean.Parameters.AddWithValue("a", a);
                await clean.ExecuteNonQueryAsync();
            }
        }
    }
}
