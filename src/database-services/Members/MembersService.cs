﻿using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.SqlServer;
using System.Linq;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Kcsara.Database.Model;
using Kcsara.Database.Model.Members;
using Sar.Model;
using Sar.Services.Auth;
using Data = Kcsar.Database.Model;

namespace Kcsara.Database.Services.Members
{
  public interface IMembersService
  {
    Task<MemberInfo> GetMember(Guid id);
    Task<IEnumerable<MemberSummary>> ByPhoneNumber(string id);
    Task<IEnumerable<MemberSummary>> ByWorkerNumber(string id);
    Task<IEnumerable<MemberSummary>> ByEmail(string id);

    Task<IEnumerable<PersonContact>> ListMemberContactsAsync(Guid memberId);
    Task<MemberInfo> CreateMember(MemberInfo body);
    Task AddMembership(Guid id, Guid statusId);
    Task<PersonContact> AddContact(Guid id, PersonContact emailContact);
    Task<int> GetEmergencyContactCountAsync(Guid memberId);
  }

  public class MembersService : IMembersService
  {
    private readonly Func<Data.IKcsarContext> _dbFactory;
    private readonly IAuthorizationService _authz;
    private readonly IAuthenticatedHost _host;

    /// <summary></summary>
    /// <param name="dbFactory"></param>
    /// <param name="authSvc"></param>
    /// <param name="host"></param>
    public MembersService(Func<Data.IKcsarContext> dbFactory, IAuthorizationService authSvc, IAuthenticatedHost host)
    {
      _dbFactory = dbFactory;
      _authz = authSvc;
      _host = host;
    }

    /// <summary></summary>
    /// <param name="id"></param>
    /// <returns></returns>
    public async Task<MemberInfo> GetMember(Guid id)
    {
      if (!await _authz.AuthorizeAsync(_host.User, id, "Read:Member")) throw new AuthorizationException();
      using (var db = _dbFactory())
      {
        var list = (await SummariesWithMemberships<MemberInfo>(db.Members.Where(f => f.Id == id), (row, member) =>
        {
          member.First = row.FirstName;
          member.Last = row.LastName;
          member.BackgroundKnown = row.BackgroundDate.HasValue;
        })).ToArray();
        if (list.Length == 0) return null;

        return list[0];
      }
    }

    public async Task<MemberInfo> CreateMember(MemberInfo member)
    {
      if (!await _authz.AuthorizeAsync(_host.User, null, "Create:Member")) throw new AuthorizationException();
      using (var db = _dbFactory())
      {
        var row = new Data.Member
        {
          FirstName = member.First,
          MiddleName = member.Middle,
          LastName = member.Last,
          DEM = member.WorkerNumber,
          Gender = member.Gender.FromModel(),
          WacLevel = member.WacLevel.FromModel(),
          WacLevelDate = member.WacLevelDate,
          BirthDate = member.BirthDate
        };
        db.Members.Add(row);
        await db.SaveChangesAsync();

        return await GetMember(row.Id);
      }
    }

    /// <summary></summary>
    /// <param name="id"></param>
    /// <returns></returns>
    public async Task<IEnumerable<MemberSummary>> ByWorkerNumber(string id)
    {
      if (!await _authz.AuthorizeAsync(_host.User, null, "Read:Member")) throw new AuthorizationException();

      id = id.TrimStart('S', 'R');

      using (var db = _dbFactory())
      {
        return await SummariesWithMemberships<MemberSummary>(db.Members.Where(f => f.DEM == id || f.DEM == "SR" + id));
      }
    }

    /// <summary></summary>
    /// <param name="id"></param>
    /// <returns></returns>
    public async Task<IEnumerable<MemberSummary>> ByPhoneNumber(string id)
    {
      if (!await _authz.AuthorizeAsync(_host.User, null, "Read:Member")) throw new AuthorizationException();
      if (id.Length < 10 || !Regex.IsMatch(id, "\\d+"))
      {
        return new MemberSummary[0];
      }

      var pattern = string.Format("%{0}%{1}%{2}%",
        id.Substring(id.Length - 10, 3),
        id.Substring(id.Length - 7, 3),
        id.Substring(id.Length - 4, 4));

      using (var db = _dbFactory())
      {
        return await SummariesWithMemberships<MemberSummary>(db.Members.Where(f => f.ContactNumbers.Any(g => SqlFunctions.PatIndex(pattern, g.Value) > 0)));
      }
    }

    /// <summary></summary>
    /// <param name="id"></param>
    /// <returns></returns>
    public async Task<IEnumerable<MemberSummary>> ByEmail(string id)
    {
      if (!await _authz.AuthorizeAsync(_host.User, null, "Read:Member")) throw new AuthorizationException();
      using (var db = _dbFactory())
      {
        return await SummariesWithMemberships<MemberSummary>(db.Members.Where(f => f.ContactNumbers.Any(g => g.Value == id && g.Type == "email")));
      }
    }

    /// <summary></summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="query"></param>
    /// <param name="modify"></param>
    /// <returns></returns>
    internal static async Task<IEnumerable<T>> SummariesWithMemberships<T>(IQueryable<Data.Member> query, Action<Data.Member, T> modify = null)
      where T : MemberSummary, new()
    {
      DateTime cutoff = DateTime.Now;

      var list = (await query
        .Select(f => new
        {
          Member = f,
          Units = f.Memberships
                   .Where(g => (g.EndTime == null || g.EndTime > cutoff) && g.Status.IsActive)
                   .Select(g => new UnitMembershipSummary
                   {
                     Unit = new NameIdPair { Id = g.Unit.Id, Name = g.Unit.DisplayName },
                     Status = g.Status.StatusName
                   }).Distinct()
        })
        .OrderBy(f => f.Member.LastName).ThenBy(f => f.Member.FirstName)
        .ToListAsync());

      return list.Select(f =>
      {
        var m = new T
        {
          Name = f.Member.FullName,
          WorkerNumber = f.Member.DEM,
          Id = f.Member.Id,
          Units = f.Units.ToArray(),
          Photo = f.Member.PhotoFile
        };
        modify?.Invoke(f.Member, m);
        return m;
      });
    }

    public Task<IEnumerable<PersonContact>> ListMemberContactsAsync(Guid memberId)
    {
      return _ListMemberContactsAsync(memberId);
    }

    /// <summary></summary>
    /// <param name="memberId"></param>
    /// <returns></returns>
    private async Task<IEnumerable<PersonContact>> _ListMemberContactsAsync(Guid memberId, Expression<Func<Data.PersonContact, bool>> predicate = null)
    {
      if (!await _authz.AuthorizeAsync(_host.User, memberId, "Read:Member")) throw new AuthorizationException();
      using (var db = _dbFactory())
      {
        var query = db.Members.Where(f => f.Id == memberId).SelectMany(f => f.ContactNumbers);
        if (predicate != null) query = query.Where(predicate);

        return await query.Select(f => new PersonContact
        {
          Id = f.Id,
          Type = f.Type,
          SubType = f.Subtype,
          Value = f.Value,
          Priority = f.Priority
        }).OrderBy(f => f.Type).ThenBy(f => f.Priority).ThenBy(f => f.SubType).ToListAsync();
      }
    }

    public Task AddMembership(Guid id, Guid statusId)
    {
      throw new NotImplementedException();
    }

    public async Task<PersonContact> AddContact(Guid id, PersonContact data)
    {
      if (!await _authz.AuthorizeAsync(_host.User, id, "Create:MemberContact@Member")) throw new AuthorizationException();

      Guid newId;
      using (var db = _dbFactory())
      {
        var row = new Data.PersonContact
        {
          PersonId = id,
          // This next line is only here for the GetReportHtml logging call
          Person = db.Members.FirstOrDefault(f => f.Id == id),
          Type = data.Type,
          Subtype = data.SubType,
          Priority = data.Priority,
          Value = data.Value,
        };

        db.PersonContact.Add(row);
        await db.SaveChangesAsync();
        newId = row.Id;
      }

      return (await _ListMemberContactsAsync(id, f => f.Id == newId)).First();
    }

    public async Task<int> GetEmergencyContactCountAsync(Guid memberId)
    {
      if (!await _authz.AuthorizeAsync(_host.User, memberId, "Read:Member")) throw new AuthorizationException();

      using (var db = _dbFactory())
      {
        return await db.Members.Where(f => f.Id == memberId).Select(f => f.EmergencyContacts.Count).SingleOrDefaultAsync();
      }
    }
  }
}
