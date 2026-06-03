using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Repositories;

public class AlloggiatiWebReportRepository(AppDbContext context) : IAlloggiatiWebReportRepository
{
    public async Task<AlloggiatiWebReport?> GetByIdAsync(Guid id)
    {
        return await context.AlloggiatiWebReports
            .Include(r => r.Booking)
            .Include(r => r.Guest)
            .FirstOrDefaultAsync(r => r.Id == id);
    }

    public async Task<AlloggiatiWebReport?> GetByBookingIdAsync(Guid bookingId)
    {
        return await context.AlloggiatiWebReports
            .Include(r => r.Guest)
            .FirstOrDefaultAsync(r => r.BookingId == bookingId);
    }

    public async Task<IEnumerable<AlloggiatiWebReport>> GetPendingReportsAsync()
    {
        return await context.AlloggiatiWebReports
            .Include(r => r.Booking)
            .Include(r => r.Guest)
            .Where(r => r.Status == AlloggiatiWebStatus.Pending || r.Status == AlloggiatiWebStatus.Failed)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync();
    }

    public async Task<AlloggiatiWebReport> AddAsync(AlloggiatiWebReport report)
    {
        context.AlloggiatiWebReports.Add(report);
        await context.SaveChangesAsync();
        return report;
    }

    public async Task<AlloggiatiWebReport> UpdateAsync(AlloggiatiWebReport report)
    {
        report.UpdatedAt = DateTime.UtcNow;
        context.AlloggiatiWebReports.Update(report);
        await context.SaveChangesAsync();
        return report;
    }
}
