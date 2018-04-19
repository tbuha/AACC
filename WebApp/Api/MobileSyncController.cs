﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using WebApp.Models;

namespace WebApp.Api
{
    public class UserInfo
    {
        public int UserId { get; set; }
        public string UserName { get; set; }
    }

    public class LoginRequest
    {
        public string UserName { get; set; }
        public string Password { get; set; }
    }

    public class LoginResult
    {
        public string Info { get; set; }
        public string Data { get; set; }
    }

    [Produces("application/json")]
    [Route("api/MobileSync")]
    public class MobileSyncController : Controller
    {
        private readonly AACCContext _context;
        private ILogger<MobileSyncController> _logger;

        public MobileSyncController(AACCContext context, ILogger<MobileSyncController> logger)
        {
            _logger = logger;
            _context = context;
        }

        [HttpPost]
        [Route("/api/MobileSync/login")]
        public async Task<LoginResult> Login([FromBody] string info)
        {
            var error = new LoginResult { Info = "Login Error" };
            try
            {
                _logger.LogInformation("User trying to login");
                _logger.LogInformation($"info '{info}'");
                var login = JsonConvert.DeserializeObject<LoginRequest>(Security.Decrypt(info));
                
                _logger.LogInformation($"User name {login.UserName} Password {login.Password}");
                var user = await _context.Assessors.SingleOrDefaultAsync(a => a.Login == login.UserName && a.Password == login.Password);
                if (user == null) return error;
                var userInfo = new UserInfo { UserId = user.AssessorId, UserName = user.Name };
                return new LoginResult { Data = Security.Crypt(JsonConvert.SerializeObject(userInfo)) };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MobileSyncLogin");
                return error;
            }
        }

        [HttpPost]
        public async Task<IActionResult> Sync([FromBody] List<Report> reports)
        {
            _logger.LogInformation("sync...");
            var model = new SyncModel();
            StringBuilder sb = new StringBuilder();
            try
            {
                try
                {
                    foreach (var report in reports)
                    {
                        if (report.AgedCareCenterId == -1 || report.AssessorId == -1) continue;

                        if (report.IsDeleted)
                        {
                            if (report.QuestionReply != null)
                                foreach (var reply in report.QuestionReply)
                                {
                                    if (reply.QuestionReplyId != 0)
                                        _context.Remove(reply);
                                }
                            _context.Remove(report);
                            await _context.SaveChangesAsync();
                        }
                        else if (report.IsNew)
                        {
                            await _context.SaveReport(report);
                        }
                        else if (report.IsChanged)
                        {
                            await _context.UpdateReport(report);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "MobileSync");
                    sb.AppendLine(ex.Message);
                }

                model.AgedCareCenterList = await _context.AgedCareCenters.ToListAsync();
                model.AssessorList = await _context.Assessors.ToListAsync();
                var Questions = await _context.Questions.GroupBy(q => q.AccreditationStandartId).ToListAsync();
                model.ReportList = await _context.Reports.Include(r => r.QuestionReply).ToListAsync();

                var newReport = new Report
                {
                    AgedCareCenterId = -1,
                    AssessorId = -1,
                    ReportDate = DateTime.Now,
                    IsNew = true
                };

                newReport.QuestionReply = Questions.SelectMany(g => g.Select((q, index) => new QuestionReply
                {
                    QuestionId = q.QuestionId,
                    QuestionNumber = $"{g.Key}.{index + 1}",
                    Response = false,
                    Question = q
                })).ToList();
                model.NewReport = newReport;
                model.ReportList
                    .ForEach(r =>
                    {
                        var questionNumberOrderBy = 0;
                        r.QuestionReply.GroupBy(qr => qr.Question.AccreditationStandartId)
                                                     .SelectMany(g => g.OrderBy(q => q.QuestionId).Select((qr, i) =>
                                                       {
                                                           questionNumberOrderBy++;
                                                           qr.QuestionNumberOrderBy = questionNumberOrderBy;
                                                           qr.QuestionNumber = $"{g.Key}.{i + 1}";
                                                           return qr.QuestionNumber;
                                                       }))
                                                     .ToList();
                        r.QuestionReply = r.QuestionReply.OrderBy(qr => qr.QuestionNumberOrderBy).ToList();
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MobileSync");
                sb.AppendLine(ex.Message);
            }
            finally
            {
                var message = sb.ToString();
                model.Error = message;
            }

            return CreatedAtAction("Sync", model);
        }
    }

    public class SyncModel
    {
        public List<AgedCareCenter> AgedCareCenterList { get; set; }
        public List<Assessor> AssessorList { get; set; }
        public List<Report> ReportList { get; set; }
        public List<AccreditationStandart> AccreditationStandartList { get; set; }
        public Report NewReport { get; set; }
        public string Error { get; set; }
    }
}