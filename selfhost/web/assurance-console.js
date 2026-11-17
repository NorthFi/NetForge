// root/selfhost/web/assurance-console.js
// Internal service-assurance views. RADIUS activity != verified availability;
// only staff-attested source-linked periods enter downtime threshold reports.
'use strict';

function assuranceCsv(records,month,target){
 const columns=[
  ['account','Account'],['username','PPPoE username'],
  ['current_service_state','Current service state'],
  ['observation_window_minutes','Observation window minutes'],
  ['staff_attested_downtime_minutes','Staff-attested downtime minutes'],
  ['internal_threshold_budget_minutes','Internal threshold budget minutes'],
  ['maximum_possible_availability_percent','Maximum possible availability %'],
  ['accounting_sessions','Recorded RADIUS sessions'],
  ['recorded_stops','Recorded session stops'],
  ['interruption_count','Attested intervals'],['finding','Finding']
 ];
 const cell=value=>{
  let raw=String(value??'');
  if(/^[=+\-@\t\r]/.test(raw))raw="'"+raw;
  return '"'+raw.replace(/"/g,'""')+'"';
 };
 return [
  [cell('Internal evidence only - NOT contractual SLA certification')],
  [cell('Period UTC'),cell(month),cell('Internal target %'),cell(target)],
  columns.map(x=>cell(x[1])),
  ...records.map(row=>columns.map(([key])=>cell(row[key])))
 ].map(row=>row.join(',')).join('\r\n')+'\r\n';
}

function assuranceConsole(el){
 el.innerHTML+='<h1>Subscriber service assurance</h1>'+
  '<p class="sub">RADIUS accounting correlation, human-attested service interruptions and internal monthly target screening. Absence of an interruption record does not establish 100% uptime.</p>'+
  '<div class="card"><div class="toolbar"><h2>Monthly evidence report</h2><button id="assurance-refresh">Refresh</button></div>'+
  '<div class="formgrid"><div><label>UTC reporting month</label><input type="month" id="assurance-month"></div>'+
  '<div><label>Customer filter</label><select id="assurance-customer"><option value="">All customers</option>'+
   (data.customers||[]).map(x=>'<option value="'+esc(x.id)+'">'+esc(x.account)+' — '+esc(x.first_name)+' '+esc(x.surname)+'</option>').join('')+
  '</select></div></div><div class="buttons"><button class="primary" id="assurance-run">Generate internal report</button><button id="assurance-export" disabled>Export CSV (internal)</button></div><p id="assurance-report-note" class="note"></p><div id="assurance-report">Select a month to generate a report.</div></div>'+
  '<div class="card topgap"><h2>Staff-attested service interruption</h2><p class="note">Must be backed by an actual highsite incident or support ticket. The explanation is an operator attestation; NetForge does not infer these intervals from missing accounting updates. Entries are append-only, non-overlapping, and never alter customer billing automatically.</p>'+
  '<form id="assurance-interruption"><div class="formgrid"><div><label>Subscriber PPPoE service</label><select name="serviceId" required><option value="">Choose customer service</option>'+
  (data.services||[]).map(s=>'<option value="'+esc(s.id)+'">'+esc(s.account)+' — '+esc(s.username)+'</option>').join('')+
  '</select></div><div><label>NOC highsite incident (or leave blank if linking a ticket)</label><select name="incidentId"><option value="">No incident</option></select></div>'+
  '<div><label>CRM ticket (or leave blank if linking a NOC incident)</label><select name="ticketId"><option value="">No ticket</option>'+
  (data.tickets||[]).map(t=>'<option value="'+esc(t.id)+'">#'+esc(t.number)+' — '+esc(t.subject)+'</option>').join('')+
  '</select></div><div><label>Start — local time</label><input type="datetime-local" name="startedAt" required></div>'+
  '<div><label>End — local time</label><input type="datetime-local" name="endedAt" required></div></div>'+
  '<label>Evidence and verification notes (20–2000 characters)</label><textarea name="evidence" rows="4" minlength="20" maxlength="2000" required placeholder="Explain who verified the interruption, which service was tested, and what evidence establishes the time window."></textarea>'+
  '<p id="assurance-attestation-error" class="error"></p><div class="buttons"><button class="primary">Record staff-attested interruption</button></div></form>'+
  '<div class="toolbar topgap"><h3>Recorded interruptions</h3><button id="assurance-reload-interruptions">Refresh</button></div><div id="assurance-interruptions">Loading…</div></div>'+
  '<div class="card topgap"><div class="toolbar"><h2>NOC incident — RADIUS evidence</h2><button id="assurance-reload-evidence">Refresh data</button></div>'+
  '<p class="note">Incident snapshots preserve the initial PPPoE session accounting state, plus explicit later refreshes. Recent/stale/terminated session states are diagnostic—not proof of internet reachability or contracted downtime.</p>'+
  '<label>Incident</label><select id="assurance-incident"><option value="">Choose incident</option></select>'+
  '<div class="buttons"><button id="assurance-load-evidence">View observations</button><button id="assurance-capture" class="primary">Capture new RADIUS evidence</button></div>'+
  '<div id="assurance-evidence">Choose an incident to display accounting observations.</div></div>'+
  (role==='admin'?'<div class="card topgap"><h2>Internal reporting policy</h2><form id="assurance-policy"><div class="formgrid"><div><label>Internal availability target (%)</label><input type="number" name="targetPercent" min="90" max="99.999" step="0.001" required></div><div><label>RADIUS accounting freshness (minutes)</label><input type="number" name="accountingFreshnessMinutes" min="2" max="120" step="1" required></div></div><p class="note">This target only screens whether staff-attested interruptions already exceed a hypothetical downtime budget. It does not certify contract compliance, generate SLA credits or confirm internet uptime.</p><p id="assurance-policy-error" class="error"></p><button class="primary">Save internal policy</button></form></div>':'');
 const month=el.querySelector('#assurance-month'),customer=el.querySelector('#assurance-customer'),
  report=el.querySelector('#assurance-report'),note=el.querySelector('#assurance-report-note'),
  exportButton=el.querySelector('#assurance-export'),
  incidents=el.querySelector('#assurance-incident'),evidence=el.querySelector('#assurance-evidence'),
  attestation=el.querySelector('#assurance-interruption'),
  error=el.querySelector('#assurance-attestation-error');
 month.value=new Date().toISOString().slice(0,7);
 let cachedReport=null;

 async function drawReport(){
  try{
   const q=new URLSearchParams({period:month.value});
   if(customer.value)q.set('customerId',customer.value);
   const result=await api('/assurance/reports/monthly?'+q.toString());
   cachedReport=result;
   exportButton.disabled=result.services.length===0;
   note.textContent=result.methodology;
   const services=result.services;
   report.innerHTML='<p><b>'+esc(result.period)+' UTC</b> · Internal threshold '+esc(result.targetPercent)+'% · '+esc(result.documentedBudgetExceeded)+' records already exceed the documented downtime budget. Every other record remains insufficient evidence for a compliant uptime claim.</p>'+
    '<div class="tablewrap"><table><thead><tr><th>Account</th><th>Service</th><th>Current status</th><th>Window (min)</th><th>Staff-attested down (min)</th><th>Budget (min)</th><th>Ceiling %</th><th>Accounting sessions</th><th>Session stops</th><th>Finding</th></tr></thead><tbody>'+
    services.map(s=>'<tr><td>'+esc(s.account)+'</td><td>'+esc(s.username)+'</td><td>'+esc(s.current_service_state)+'</td><td>'+esc(s.observation_window_minutes)+'</td><td>'+esc(s.staff_attested_downtime_minutes)+'</td><td>'+esc(s.internal_threshold_budget_minutes)+'</td><td>'+esc(s.maximum_possible_availability_percent??'Unknown')+'</td><td>'+esc(s.accounting_sessions)+'</td><td>'+esc(s.recorded_stops)+'</td><td>'+esc(s.finding)+'</td></tr>').join('')+
    '</tbody></table></div>';
  }catch(err){cachedReport=null;exportButton.disabled=true;report.textContent=err.message}
 }
 async function drawInterruptions(){
  const box=el.querySelector('#assurance-interruptions');
  try{
   const rows=await api('/assurance/interruptions');
   box.innerHTML='<div class="tablewrap"><table><thead><tr><th>Account</th><th>PPPoE</th><th>Start UTC</th><th>End UTC</th><th>Minutes</th><th>Source</th><th>Verified by</th><th>Evidence</th></tr></thead><tbody>'+
    rows.map(x=>'<tr><td>'+esc(x.account)+'</td><td>'+esc(x.username)+'</td><td>'+esc(x.started_at)+'</td><td>'+esc(x.ended_at)+'</td><td>'+esc(x.documented_minutes)+'</td><td>'+esc(x.incident_id?'NOC incident': 'CRM ticket')+'</td><td>'+esc(x.verified_by)+'</td><td>'+esc(x.evidence)+'</td></tr>').join('')+
    '</tbody></table></div>'+(rows.length?'':'<p>No staff-attested interruptions recorded.</p>');
  }catch(err){box.textContent=err.message}
 }
 async function loadIncidents(){
  try{
   const rows=await api('/noc/incidents?state=all');
   incidents.innerHTML='<option value="">Choose incident</option>'+
    rows.map(i=>'<option value="'+esc(i.id)+'">'+esc(i.highsite)+' · '+esc(i.opened_at)+' · '+esc(i.status)+'</option>').join('');
   attestation.querySelector('[name="incidentId"]').innerHTML=incidents.innerHTML;
  }catch(err){evidence.textContent=err.message}
 }
 async function drawEvidence(){
  if(!incidents.value){evidence.textContent='Choose a highsite incident first.';return}
  evidence.textContent='Loading service accounting evidence…';
  try{
   const result=await api('/assurance/incidents/'+incidents.value+'/evidence?limit=500');
   evidence.innerHTML='<p class="note">'+esc(result.interpretation)+'</p><p>Services in original site exposure snapshot: '+esc(result.total)+' (showing up to 500).</p>'+
    '<div class="tablewrap"><table><thead><tr><th>Customer</th><th>PPPoE service</th><th>State at incident</th><th>RADIUS observation</th><th>Observed at UTC</th><th>Active session records</th><th>Recent accounting</th><th>Last accounting</th><th>Last session stop</th><th>Stop reason</th><th>Actions</th></tr></thead><tbody>'+
    result.records.map(row=>'<tr><td>'+esc(row.account)+'</td><td>'+esc(row.username_snapshot)+'</td><td>'+esc(row.service_state_snapshot)+'</td><td>'+esc(row.evidence_state||'Unobserved')+'</td><td>'+esc(row.observed_at||'Never')+'</td><td>'+esc(row.open_sessions??'—')+'</td><td>'+esc(row.recent_open_sessions??'—')+'</td><td>'+esc(row.last_accounting_at||'—')+'</td><td>'+esc(row.last_session_stop_at||'—')+'</td><td>'+esc(row.stop_reason||'—')+'</td><td><button data-evidence-history="'+esc(row.service_id)+'">History</button></td></tr>').join('')+'</tbody></table></div>';
   evidence.querySelectorAll('[data-evidence-history]').forEach(b=>b.onclick=async()=>{
    try{
     const history=await api('/assurance/incidents/'+incidents.value+
      '/evidence/'+b.dataset.evidenceHistory+'/history');
     const d=dialog('<div class="toolbar"><h2>RADIUS accounting evidence history</h2><button id="close">Close</button></div><p class="note">Repeated observations are independent samples and are not outage-duration measurements.</p>'+
      '<div class="tablewrap"><table><thead><tr><th>UTC</th><th>Evidence</th><th>Open sessions</th><th>Fresh updates</th><th>Latest update</th><th>Latest stop</th></tr></thead><tbody>'+
      history.map(x=>'<tr><td>'+esc(x.observed_at)+'</td><td>'+esc(x.state)+'</td><td>'+esc(x.open_sessions)+'</td><td>'+esc(x.recent_open_sessions)+'</td><td>'+esc(x.last_accounting_at||'—')+'</td><td>'+esc(x.last_session_stop_at||'—')+'</td></tr>').join('')+'</tbody></table></div>');
     d.querySelector('#close').onclick=()=>d.close();
    }catch(err){evidence.textContent=err.message}
   });
  }catch(err){evidence.textContent=err.message}
 }
 attestation.onsubmit=async e=>{
  e.preventDefault();error.textContent='';
  const values=Object.fromEntries(new FormData(attestation));
  if(!values.incidentId&&!values.ticketId){
   error.textContent='A linked NOC incident or CRM ticket is required.';return;
  }
  const start=new Date(values.startedAt),end=new Date(values.endedAt);
  if(!Number.isFinite(start.getTime())||!Number.isFinite(end.getTime())){
   error.textContent='Choose valid start and end times.';return;
  }
  if(!confirm('Attest this subscriber interruption with evidence? The immutable entry influences internal monthly downtime reports, but does not trigger billing credits.'))return;
  try{
   await api('/assurance/interruptions','POST',{
    serviceId:values.serviceId,incidentId:values.incidentId||null,
    ticketId:values.ticketId||null,
    startedAt:start.toISOString(),endedAt:end.toISOString(),
    evidence:values.evidence,confirm:true
   });
   attestation.reset();error.textContent='Internal interruption attestation saved.';
   await Promise.all([drawReport(),drawInterruptions()]);
  }catch(err){error.textContent=err.message}
 };
 el.querySelector('#assurance-run').onclick=drawReport;
 el.querySelector('#assurance-refresh').onclick=()=>Promise.all([
  drawReport(),drawInterruptions(),loadIncidents()
 ]);
 el.querySelector('#assurance-reload-interruptions').onclick=drawInterruptions;
 el.querySelector('#assurance-reload-evidence').onclick=loadIncidents;
 el.querySelector('#assurance-load-evidence').onclick=drawEvidence;
 el.querySelector('#assurance-capture').onclick=async()=>{
  if(!incidents.value){evidence.textContent='Choose an incident.';return}
  if(!confirm('Capture new RADIUS accounting observations for the original incident services? Missing accounting is not downtime.'))return;
  try{
   const result=await api('/assurance/incidents/'+incidents.value+
    '/refresh','POST',{confirm:true});
   await drawEvidence();
   notice('Captured '+result.captured+' new accounting observations.');
  }catch(err){evidence.textContent=err.message}
 };
 exportButton.onclick=()=>{
  if(!cachedReport)return;
  const csv=assuranceCsv(cachedReport.services,cachedReport.period,cachedReport.targetPercent);
  const blob=new Blob([csv],{type:'text/csv;charset=utf-8'});
  const url=URL.createObjectURL(blob);
  const a=document.createElement('a');
  a.href=url;a.download='netforge-assurance-'+cachedReport.period+'-internal.csv';
  a.click();
  URL.revokeObjectURL(url);
 };
 if(role==='admin'){
  const form=el.querySelector('#assurance-policy');
  api('/assurance/policy').then(settings=>{
   form.querySelector('[name="targetPercent"]').value=settings.settings.target_percent;
   form.querySelector('[name="accountingFreshnessMinutes"]').value=
    settings.settings.accounting_freshness_minutes;
  }).catch(err=>{form.querySelector('#assurance-policy-error').textContent=err.message});
  form.onsubmit=async e=>{
   e.preventDefault();const values=Object.fromEntries(new FormData(form));
   if(!confirm('Change the INTERNAL assurance screening target and accounting freshness? It is not an SLA contract modification.'))return;
   try{
    await api('/assurance/policy','PUT',{
     targetPercent:Number(values.targetPercent),
     accountingFreshnessMinutes:Number(values.accountingFreshnessMinutes),
     confirm:true
    });notice('Internal assurance policy updated.');await drawReport();
   }catch(err){form.querySelector('#assurance-policy-error').textContent=err.message}
  };
 }
 Promise.all([drawReport(),drawInterruptions(),loadIncidents()]).catch(err=>notice(err.message,true));
}
