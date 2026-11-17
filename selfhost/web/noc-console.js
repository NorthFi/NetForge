// root/selfhost/web/noc-console.js
// Staff-only incident triage. All untrusted data is HTML-escaped before display.
// Showing potentially exposed customers does not imply confirmed disconnection.
'use strict';

function nocConsole(el){
 el.innerHTML+='<h1>NOC incident operations</h1><p class="sub">Potential highsite outages inferred from explicitly tagged upstream TCP monitoring, plus manually reported service incidents. No automatic subscriber messages, RADIUS changes or credit adjustments.</p>'+
  '<div id="noc-summary" class="grid"><div class="card">Loading incident counters…</div></div>'+
  '<div class="card topgap"><div class="toolbar"><h2>Incident register</h2><div class="buttons"><select id="noc-filter" aria-label="Incident filter"><option value="active">Unresolved</option><option value="resolved">Resolved</option><option value="all">All</option></select><button id="noc-reload">Refresh</button><button id="noc-manual" class="primary">Report incident</button></div></div><div id="noc-register">Loading…</div></div>'+
  '<div class="card topgap"><p class="note">Exposure counts reflect customer assignments at incident detection time. Automatic incident creation and recovery are driven by critical upstream TCP status, not confirmed PPPoE failures or a contractual uptime calculation. Customer messaging remains separate and disabled unless explicitly implemented and approved.</p></div>';
 const summary=el.querySelector('#noc-summary'),table=el.querySelector('#noc-register'),filter=el.querySelector('#noc-filter');
 async function reload(){
  try{
   const [overview,incidents]=await Promise.all([
    api('/noc/summary'),api('/noc/incidents?state='+encodeURIComponent(filter.value))
   ]);
   const totals=overview.summary;
   const metrics=[
    ['Open',totals.open],['Acknowledged',totals.acknowledged],
    ['Awaiting verification',totals.monitoring],
    ['Potentially exposed customers',totals.potential_customer_exposure]
   ];
   summary.innerHTML=metrics.map(([label,value])=>
    '<div class="card"><small>'+esc(label)+'</small><div class="metric">'+esc(value)+'</div></div>').join('');
   table.innerHTML='<div class="tablewrap"><table><thead><tr><th>Opened (UTC)</th><th>Highsite</th><th>Summary</th><th>Severity</th><th>Workflow</th><th>Upstream recovered</th><th>Assigned to</th><th>Potential customers</th><th>CRM ticket</th><th>Action</th></tr></thead><tbody>'+
    incidents.map(i=>'<tr><td>'+esc(i.opened_at)+'</td><td>'+esc(i.highsite)+'</td><td>'+esc(i.summary)+'</td><td>'+esc(i.severity)+'</td><td>'+esc(i.status)+'</td><td>'+esc(i.recovered_at?'Yes':'No')+'</td><td>'+esc(i.assigned_to||'Unassigned')+'</td><td>'+esc(i.potential_customers)+'</td><td>'+esc(i.ticket_number??'—')+'</td><td><button data-noc-detail="'+esc(i.id)+'">Investigate</button></td></tr>').join('')+'</tbody></table></div>'+
    (incidents.length?'':'<p>No incidents for this filter.</p>');
   table.querySelectorAll('[data-noc-detail]').forEach(button=>button.onclick=()=>{
    const entry=incidents.find(x=>x.id===button.dataset.nocDetail);
    if(entry)nocDetails(entry,reload);
   });
  }catch(error){table.textContent=error.message}
 }
 el.querySelector('#noc-manual').onclick=()=>nocManualReport(reload);
 el.querySelector('#noc-reload').onclick=reload;
 filter.onchange=reload;
 reload();
}

function nocManualReport(done){
 const sites=data.highsites||[];
 const d=dialog('<h2>Report potential site incident</h2><p class="note">This creates an internal NOC record with a snapshot of active customers. It does not prove downtime or send subscriber notifications.</p><form id="noc-report"><label>Highsite</label><select name="highsiteId" required><option value="">Choose highsite</option>'+
  sites.map(s=>'<option value="'+esc(s.id)+'">'+esc(s.number)+' — '+esc(s.name)+'</option>').join('')+
  '</select><label>Summary</label><input name="summary" maxlength="200" minlength="5" required><label>Severity</label><select name="severity"><option value="normal">Normal</option><option value="high">High</option><option value="critical">Critical</option><option value="low">Low</option></select><p class="error" id="noc-report-error"></p><div class="buttons"><button class="primary">Create internal incident</button><button type="button" id="noc-cancel">Cancel</button></div></form>');
 d.querySelector('#noc-cancel').onclick=()=>d.close();
 d.querySelector('#noc-report').onsubmit=async e=>{
  e.preventDefault();
  const values=Object.fromEntries(new FormData(e.target));
  if(!confirm('Create an internal incident for '+values.highsiteId+'? Customer accounts will only be marked as potentially exposed.'))return;
  try{
   await api('/noc/incidents','POST',{...values,confirm:true});
   d.close();await done();
  }catch(error){d.querySelector('#noc-report-error').textContent=error.message}
 };
}

function nocTicketLink(incident,after){
 const valid=(data.tickets||[]).filter(t=>
  t.highsite_id===incident.highsite_id && t.type==='highsite');
 const d=dialog('<h2>CRM highsite ticket</h2><p class="note">Creating or linking a staff ticket is manual. No customer portal message or external notification will be sent.</p><form id="noc-ticket-form"><label>Existing highsite ticket (optional)</label><select name="ticketId"><option value="">Create new highsite ticket</option>'+
  valid.map(t=>'<option value="'+esc(t.id)+'">#'+esc(t.number)+' '+esc(t.subject)+'</option>').join('')+
  '</select><label>New ticket subject (optional)</label><input name="subject" maxlength="200"><label>New ticket description (optional)</label><textarea name="description" maxlength="2000" rows="3"></textarea><p id="noc-ticket-error" class="error"></p><div class="buttons"><button class="primary">Create or link ticket</button><button type="button" id="cancel">Cancel</button></div></form>');
 d.querySelector('#cancel').onclick=()=>d.close();
 d.querySelector('#noc-ticket-form').onsubmit=async e=>{
  e.preventDefault();const values=Object.fromEntries(new FormData(e.target));
  if(!confirm('Link a highsite CRM ticket to this NOC incident?'))return;
  try{
   await api('/noc/incidents/'+incident.id+'/ticket','POST',{
    ticketId:values.ticketId||null,subject:values.subject,
    description:values.description,confirm:true});
   d.close();await after();
  }catch(error){d.querySelector('#noc-ticket-error').textContent=error.message}
 };
}

async function nocDetails(incident,done){
 const d=dialog('<div class="toolbar"><h2>NOC incident investigation</h2><button id="noc-close">Close</button></div><div id="noc-detail">Loading incident…</div><p id="noc-action-error" class="error" role="status"></p>');
 d.querySelector('#noc-close').onclick=()=>d.close();
 const section=d.querySelector('#noc-detail'),errors=d.querySelector('#noc-action-error');
 let offset=0;
 async function load(){
  try{
   const [fresh,history,customerPage]=await Promise.all([
    api('/noc/incidents/'+incident.id),
    api('/noc/incidents/'+incident.id+'/history'),
    api('/noc/incidents/'+incident.id+'/customers?offset='+offset+'&limit=100')
   ]);
   const active=fresh.status!=='resolved';
   section.innerHTML='<p class="sub"><b>'+esc(fresh.highsite)+'</b> · '+esc(fresh.summary)+'</p>'+
    '<div class="grid"><div class="card"><small>Status</small><div class="metric">'+esc(fresh.status)+'</div></div>'+
    '<div class="card"><small>Potential customers at start</small><div class="metric">'+esc(fresh.potential_customers)+'</div></div>'+
    '<div class="card"><small>Sectors at start</small><div class="metric">'+esc(fresh.sectors_snapshot)+'</div></div></div>'+
    '<p class="muted">Opened: '+esc(fresh.opened_at)+' · Upstream recovered: '+esc(fresh.recovered_at||'Not yet detected')+' · Assigned: '+esc(fresh.assigned_to||'Unassigned')+' · CRM ticket: '+esc(fresh.ticket_number??'Not linked')+'</p>'+
    (fresh.resolution_note?'<p><b>Resolution:</b> '+esc(fresh.resolution_note)+'</p>':'')+
    '<div class="buttons">'+
    (fresh.status==='open'?'<button data-noc-action="ack">Acknowledge</button>':'')+
    (active?'<button data-noc-action="assign">Assign staff</button> <button data-noc-action="resolve">Confirm resolution</button>':'')+
    '<button data-noc-action="note">Internal note</button>'+
    (fresh.ticket_id?'':' <button data-noc-action="ticket">Link CRM ticket</button>')+
    '</div><h3>Customer exposure snapshot</h3><p class="note">Assignments at the time of incident detection. This is not a live RADIUS disconnect list, and the current site/status may differ.</p>'+
    '<div class="tablewrap"><table><thead><tr><th>Account</th><th>Customer</th><th>Active services at detection</th><th>Current status</th><th>Still assigned to site</th></tr></thead><tbody>'+
    customerPage.customers.map(c=>'<tr><td>'+esc(c.account_snapshot)+'</td><td>'+esc(c.first_name_snapshot+' '+c.surname_snapshot)+'</td><td>'+esc(c.active_services)+'</td><td>'+esc(c.current_status)+'</td><td>'+esc(c.current_site===fresh.highsite_id?'Yes':'No')+'</td></tr>').join('')+
    '</tbody></table></div><div class="buttons">'+
    (offset>0?'<button id="noc-prev">Previous 100</button>':'')+
    (offset+customerPage.customers.length<Number(fresh.potential_customers)?'<button id="noc-next">Next 100</button>':'')+
    '</div><h3>Internal audit timeline</h3><div class="tablewrap"><table><thead><tr><th>UTC</th><th>Event</th><th>By</th><th>Detail</th></tr></thead><tbody>'+
    history.map(h=>'<tr><td>'+esc(h.occurred_at)+'</td><td>'+esc(h.action)+'</td><td>'+esc(h.actor)+'</td><td>'+esc(h.detail)+'</td></tr>').join('')+
    '</tbody></table></div>';
   section.querySelector('#noc-prev')?.addEventListener('click',()=>{offset=Math.max(0,offset-100);load()});
   section.querySelector('#noc-next')?.addEventListener('click',()=>{offset+=100;load()});
   section.querySelectorAll('[data-noc-action]').forEach(button=>button.onclick=async()=>{
    const action=button.dataset.nocAction;
    try{
     if(action==='ack'){
      if(!confirm('Acknowledge this potential incident under your staff account?'))return;
      await api('/noc/incidents/'+incident.id+'/acknowledge','POST',{});
     }else if(action==='assign'){
      const name=prompt('Assign to an existing enabled admin, dispatcher or technician username (blank to clear):',fresh.assigned_to||'');
      if(name===null)return;
      await api('/noc/incidents/'+incident.id+'/assign','POST',{username:name.trim()||null});
     }else if(action==='note'){
      const note=prompt('Internal NOC note (not visible in customer portal):');
      if(note===null)return;
      await api('/noc/incidents/'+incident.id+'/notes','POST',{note});
     }else if(action==='resolve'){
      if(fresh.origin==='monitor'&&!fresh.recovered_at){
       errors.textContent='Detected upstream is still DOWN. Confirm recovery before resolving.';return;
      }
      const reason=prompt('NOC resolution reason (minimum 8 characters):');
      if(reason===null)return;
      if(!confirm('Resolve this incident based on verified service recovery?'))return;
      await api('/noc/incidents/'+incident.id+'/resolve','POST',{resolution:reason,confirm:true});
     }else if(action==='ticket'){
      nocTicketLink(fresh,async()=>{await load();await done()});return;
     }
     errors.textContent='';
     await load();await done();
    }catch(error){errors.textContent=error.message}
   });
  }catch(error){errors.textContent=error.message}
 }
 await load();
}
