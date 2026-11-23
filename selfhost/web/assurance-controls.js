// root/selfhost/web/assurance-controls.js
// Administrative SLA versions, two-person credit approval, and safe CPE probes.
// Read-only customer management access is not proof of subscriber Internet uptime.
'use strict';

function slaCreditConsole(el){
 el.innerHTML+='<h1>SLA agreements and controlled credits</h1>'+
 '<p class="sub">Immutable dated internal SLA terms, documented interruption references and a dual-control invoice-credit workflow. Proposal and approval never post to the ledger.</p>'+
 '<div class="card"><h2>Record SLA version</h2><form id="sla-terms"><div class="formgrid">'+
 '<div><label>PPPoE service</label><select name="serviceId" id="sla-service" required></select></div>'+
 '<div><label>Effective date (UTC)</label><input type="date" name="effectiveFrom" id="sla-date" required></div>'+
 '<div><label>Availability target (%)</label><input type="number" name="targetPercent" step=".001" min="90" max="99.999" value="99.5" required></div>'+
 '<div><label>Maximum invoice credit (%)</label><input type="number" name="creditCapPercent" step=".01" min="0" max="100" value="10" required></div>'+
 '<div><label>Agreement/document reference</label><input name="agreementReference" minlength="5" maxlength="160" required></div>'+
 '</div><label>Agreed exclusions/conditions</label><textarea name="exclusions" rows="3" maxlength="2000"></textarea>'+
 '<p class="note">A version is immutable and takes effect no earlier than today (UTC). A reference does not constitute an electronic signature or a verified customer contract. New terms must have a later effective date than previous versions.</p>'+
 '<button class="primary">Add immutable SLA version</button><p id="sla-error" class="error"></p></form></div>'+
 '<div class="card topgap"><h2>SLA versions</h2><div id="sla-list">Loading…</div></div>'+
 '<div class="card topgap"><h2>Propose service credit</h2><form id="sla-credit"><div class="formgrid">'+
 '<div><label>Verified interruption</label><select name="interruptionId" id="credit-interruption" required></select></div>'+
 '<div><label>Issued customer invoice</label><select name="invoiceId" id="credit-invoice" required></select></div>'+
 '<div><label>Applicable SLA version</label><select name="slaVersionId" id="credit-terms" required></select></div>'+
 '<div><label>Proposed amount (ZAR)</label><input type="number" name="amount" min=".01" max="9999999999" step=".01" required></div></div>'+
 '<label>Justification (12–300 characters)</label><textarea name="reason" minlength="12" maxlength="300" required rows="2"></textarea>'+
 '<p class="note">The proposed amount must be within the versioned cap and current invoice outstanding balance. A different administrator must approve it; posting remains a separate explicit action.</p>'+
 '<button class="primary">Propose credit — do not post</button><p id="credit-error" class="error"></p></form></div>'+
 '<div class="card topgap"><div class="toolbar"><h2>Credit approval register</h2><button id="credit-refresh">Refresh</button></div><div id="credit-requests">Loading…</div></div>';
 const forms={terms:el.querySelector('#sla-terms'),credit:el.querySelector('#sla-credit')};
 if(role!=='admin')forms.terms.closest('.card').hidden=true;
 const items={services:[],invoices:[],versions:[],interruptions:[],requests:[]};
 const serviceSelect=el.querySelector('#sla-service'),
  interruptionSelect=el.querySelector('#credit-interruption'),
  invoiceSelect=el.querySelector('#credit-invoice'),
  versionSelect=el.querySelector('#credit-terms');
 el.querySelector('#sla-date').value=new Date().toISOString().slice(0,10);
 function option(value,label){return '<option value="'+esc(value)+'">'+esc(label)+'</option>'}
 function options(list,fn){return option('','Choose record')+list.map(fn).join('')}
 function syncProposal(){
  const chosen=items.interruptions.find(x=>x.id===interruptionSelect.value);
  const service=chosen?.service_id;
  const matchingInvoices=items.invoices.filter(x=>x.customer_id===items.services.find(s=>s.id===service)?.customer_id);
  const selectedInvoice=invoiceSelect.value,selectedTerms=versionSelect.value;
  invoiceSelect.innerHTML=options(matchingInvoices,x=>option(x.id,x.number+' — '+(x.outstanding??'unknown')+' ZAR outstanding'));
  if(matchingInvoices.some(x=>x.id===selectedInvoice))invoiceSelect.value=selectedInvoice;
  const versions=items.versions.filter(x=>x.service_id===service);
  versionSelect.innerHTML=options(versions,x=>option(x.id,'v'+x.version+' from '+x.effective_from+' · cap '+x.credit_cap_percent+'%'));
  if(versions.some(x=>x.id===selectedTerms))versionSelect.value=selectedTerms;
 }
 interruptionSelect.onchange=syncProposal;
 async function reload(){
  try{
   const [services,invoices,versions,interruptions,requests]=await Promise.all([
    api('/services'),api('/invoices'),api('/assurance/sla-versions'),
    api('/assurance/interruptions'),api('/assurance/credit-requests')
   ]);
   Object.assign(items,{services,invoices,versions,interruptions,requests});
   serviceSelect.innerHTML=options(services,s=>option(s.id,s.account+' · '+s.username));
   const prior=interruptionSelect.value;
   interruptionSelect.innerHTML=options(interruptions,x=>option(x.id,x.account+' · '+x.username+' · '+x.started_at));
   if(interruptions.some(x=>x.id===prior))interruptionSelect.value=prior;
   syncProposal();
   el.querySelector('#sla-list').innerHTML='<div class="tablewrap"><table><thead><tr><th>Account</th><th>Service</th><th>Version</th><th>UTC effective</th><th>Target</th><th>Credit cap</th><th>Reference</th><th>Exclusions</th></tr></thead><tbody>'+
    versions.map(v=>'<tr><td>'+esc(v.account)+'</td><td>'+esc(v.username)+'</td><td>'+esc(v.version)+'</td><td>'+esc(v.effective_from)+'</td><td>'+esc(v.target_percent)+'%</td><td>'+esc(v.credit_cap_percent)+'%</td><td>'+esc(v.agreement_reference)+'</td><td>'+esc(v.exclusions)+'</td></tr>').join('')+'</tbody></table></div>';
   const creditBox=el.querySelector('#credit-requests');
   creditBox.innerHTML='<div class="tablewrap"><table><thead><tr><th>Account</th><th>Invoice</th><th>Amount</th><th>Originator</th><th>Status</th><th>Reviewer</th><th>Reference</th><th>Actions</th></tr></thead><tbody>'+
    requests.map(x=>'<tr><td>'+esc(x.account)+'</td><td>'+esc(x.invoice)+'</td><td>'+esc(x.amount)+'</td><td>'+esc(x.proposed_by)+'</td><td>'+esc(x.status)+'</td><td>'+esc(x.reviewed_by||'—')+'</td><td>'+esc(x.agreement_reference)+'</td><td>'+
    (role==='admin'&&x.status==='proposed'?'<button data-review="'+esc(x.id)+'">Review</button>':'')+
    (role==='admin'&&x.status==='approved'?'<button data-post="'+esc(x.id)+'">Post approved credit</button>':'')+
    '</td></tr>').join('')+'</tbody></table></div>';
   creditBox.querySelectorAll('[data-review]').forEach(button=>button.onclick=async()=>{
    const request=requests.find(x=>x.id===button.dataset.review);
    if(!request)return;
    if(request.proposed_by.toLowerCase()===username.toLowerCase()){
     notice('The proposal originator cannot approve or reject it. Use a different administrator.',true);return;
    }
    const decision=confirm('Approve credit of '+request.amount+' ZAR for invoice '+request.invoice+'? Cancel opens rejection workflow.');
    const reason=prompt(decision?'Review justification for APPROVAL (8+ characters):':'Reason for REJECTION (8+ characters):');
    if(reason===null)return;
    if(!confirm((decision?'Approve':'Reject')+' this proposal without posting any ledger entry?'))return;
    try{await api('/assurance/credit-requests/'+request.id+'/review','POST',
      {approve:decision,reason,confirm:true});await reload();}
    catch(error){notice(error.message,true)}
   });
   creditBox.querySelectorAll('[data-post]').forEach(button=>button.onclick=async()=>{
    const request=requests.find(x=>x.id===button.dataset.post);
    if(!request)return;
    if(!confirm('POST '+request.amount+' ZAR service credit to invoice '+request.invoice+
     '? This writes an immutable credit note and balanced ledger journal. It cannot be undone by dismissing this dialog.'))return;
    try{const result=await api('/assurance/credit-requests/'+request.id+'/post','POST',{confirm:true});
      notice('Credit posting: '+result.status+' · remaining '+result.remaining+' ZAR');await reload();}
    catch(error){notice(error.message,true)}
   });
  }catch(error){el.querySelector('#credit-requests').textContent=error.message}
 }
 forms.terms.onsubmit=async e=>{
  e.preventDefault();
  const v=Object.fromEntries(new FormData(forms.terms));
  if(!confirm('Append an immutable INTERNAL SLA version? Incorrect historical versions cannot be edited.'))return;
  try{
   await api('/assurance/sla-versions','POST',{serviceId:v.serviceId,
    effectiveFrom:v.effectiveFrom,targetPercent:Number(v.targetPercent),
    creditCapPercent:Number(v.creditCapPercent),
    agreementReference:v.agreementReference,exclusions:v.exclusions,confirm:true});
   forms.terms.reset();el.querySelector('#sla-date').value=new Date().toISOString().slice(0,10);
   el.querySelector('#sla-error').textContent='Version saved.';await reload();
  }catch(err){el.querySelector('#sla-error').textContent=err.message}
 };
 forms.credit.onsubmit=async e=>{
  e.preventDefault();const v=Object.fromEntries(new FormData(forms.credit));
  const interruption=items.interruptions.find(x=>x.id===v.interruptionId);
  if(!interruption){el.querySelector('#credit-error').textContent='Select an interruption.';return}
  if(!confirm('Create a proposed credit ONLY? Approval and posting require additional actions.'))return;
  try{
   await api('/assurance/credit-requests','POST',{
    serviceId:interruption.service_id,interruptionId:v.interruptionId,
    slaVersionId:v.slaVersionId,invoiceId:v.invoiceId,
    amount:Number(v.amount),reason:v.reason,confirm:true});
   forms.credit.reset();el.querySelector('#credit-error').textContent='Unposted proposal created.';
   await reload();
  }catch(err){el.querySelector('#credit-error').textContent=err.message}
 };
 el.querySelector('#credit-refresh').onclick=reload;reload();
}

function cpeAssuranceConsole(el){
 el.innerHTML+='<h1>CPE management reachability</h1>'+
 '<p class="sub">Opt-in TCP checks from the NetForge server to a subscriber-owned, inventoried CPE/router management port. This does NOT test client-side web browsing, routing, DNS, or contractual Internet uptime.</p>'+
 '<div class="card"><h2>Register read-only CPE probe</h2><form id="cpe-add"><div class="formgrid"><div><label>PPPoE service</label><select name="serviceId" id="cpe-service" required></select></div>'+
 '<div><label>Customer-owned CPE/router</label><select name="equipmentId" id="cpe-equipment" required></select></div>'+
 '<div><label>TCP management port</label><select name="tcpPort"><option value="8291">8291 — RouterOS Winbox</option><option value="8729">8729 — RouterOS API TLS</option><option value="443">443 — HTTPS</option><option value="80">80 — HTTP</option><option value="22">22 — SSH</option></select></div>'+
 '<div><label>Check interval, seconds</label><input name="intervalSeconds" type="number" min="60" max="3600" value="300" required></div></div>'+
 '<label><input name="enabled" type="checkbox" style="width:auto"> Enable periodic TCP checks (default disabled)</label>'+
 '<p class="note">The CPE must have a saved IPv4 management address and be assigned to the same customer as the selected PPPoE service. Never expose a CPE management port to the public internet merely to support monitoring: use a routed private management network and restrictive firewalls.</p>'+
 '<button class="primary">Register CPE probe</button><p id="cpe-error" class="error"></p></form></div>'+
 '<div class="card topgap"><div class="toolbar"><h2>Service CPE checks</h2><button id="cpe-refresh">Refresh</button></div><div id="cpe-results">Loading…</div></div>'+
 '<div class="card topgap"><h2>Individual CPE check history</h2><div id="cpe-history">Choose a probe above.</div></div>';
 const form=el.querySelector('#cpe-add'),service=el.querySelector('#cpe-service'),
  equipment=el.querySelector('#cpe-equipment'),results=el.querySelector('#cpe-results'),
  history=el.querySelector('#cpe-history'),error=el.querySelector('#cpe-error');
 if(role!=='admin')form.closest('.card').hidden=true;
 let services=[],inventory=[];
 function populateEquipment(){
  const item=services.find(x=>x.id===service.value);
  equipment.innerHTML='<option value="">Select registered CPE</option>'+
   inventory.filter(x=>x.customer_id===item?.customer_id&&
     ['cpe','router'].includes(x.category)&&x.management_ip)
    .map(x=>'<option value="'+esc(x.id)+'">'+esc(x.name)+' · '+esc(x.management_ip)+'</option>').join('');
 }
 service.onchange=populateEquipment;
 async function reload(){
  try{
   const [entries,serviceList,assets]=await Promise.all([
    api('/assurance/cpe-probes'),api('/services'),api('/equipment')
   ]);
   services=serviceList;inventory=assets;
   const selected=service.value;
   service.innerHTML='<option value="">Choose customer service</option>'+services.map(x=>
    '<option value="'+esc(x.id)+'">'+esc(x.account)+' · '+esc(x.username)+'</option>').join('');
   if(services.some(x=>x.id===selected))service.value=selected;
   populateEquipment();
   results.innerHTML='<div class="tablewrap"><table><thead><tr><th>Customer</th><th>Service</th><th>CPE</th><th>IP/port</th><th>Polling</th><th>State</th><th>Failures</th><th>Last checked</th><th>Latency ms</th><th>Action</th></tr></thead><tbody>'+
    entries.map(x=>'<tr><td>'+esc(x.account)+'</td><td>'+esc(x.username)+'</td><td>'+esc(x.equipment)+'</td><td>'+esc(x.address)+':'+esc(x.tcp_port)+'</td><td>'+esc(x.enabled?'Enabled':'Disabled')+'</td><td>'+esc(x.state)+'</td><td>'+esc(x.failure_streak)+'</td><td>'+esc(x.last_probed_at||'Never')+'</td><td>'+esc(x.last_latency_ms??'—')+'</td><td><button data-history="'+esc(x.id)+'">History</button>'+
    (role==='admin'?' <button data-check="'+esc(x.id)+'">Check now</button> <button data-toggle="'+esc(x.id)+'">'+(x.enabled?'Disable':'Enable')+'</button>':'')+
    '</td></tr>').join('')+'</tbody></table></div>';
   results.querySelectorAll('[data-history]').forEach(btn=>btn.onclick=async()=>{
    const p=entries.find(x=>x.id===btn.dataset.history);if(!p)return;
    try{
     const samples=await api('/assurance/cpe-probes/'+p.id+'/history?hours=168');
     history.innerHTML='<h3>'+esc(p.account)+' / '+esc(p.equipment)+'</h3><p class="note">Server-to-management TCP observations; no inferred contractual outage minutes.</p>'+
      '<div class="tablewrap"><table><thead><tr><th>UTC</th><th>Connected</th><th>RTT ms</th><th>Debounced state</th></tr></thead><tbody>'+
      samples.slice(-200).reverse().map(x=>'<tr><td>'+esc(x.observed_at)+'</td><td>'+esc(x.reachable?'Yes':'No')+'</td><td>'+esc(x.latency_ms??'—')+'</td><td>'+esc(x.state)+'</td></tr>').join('')+'</tbody></table></div>';
    }catch(e){history.textContent=e.message}
   });
   results.querySelectorAll('[data-check]').forEach(btn=>btn.onclick=async()=>{
    try{const r=await api('/assurance/cpe-probes/'+btn.dataset.check+'/check','POST',{});
     notice('CPE probe: '+r.state+'; '+r.note);await reload();}
    catch(e){error.textContent=e.message}
   });
   results.querySelectorAll('[data-toggle]').forEach(btn=>btn.onclick=async()=>{
    const p=entries.find(x=>x.id===btn.dataset.toggle);if(!p)return;
    const enabled=!p.enabled;
    if(enabled&&!confirm('Enable management TCP checks from NetForge to '+p.address+':'+p.tcp_port+'?'))return;
    try{await api('/assurance/cpe-probes/'+p.id,'PUT',{
      tcpPort:p.tcp_port,intervalSeconds:p.interval_seconds,enabled,confirm:enabled
     });await reload()}
    catch(e){error.textContent=e.message}
   });
  }catch(e){results.textContent=e.message}
 }
 form.onsubmit=async e=>{
  e.preventDefault();const v=Object.fromEntries(new FormData(form));
  const enabled=form.querySelector('[name="enabled"]').checked;
  if(enabled&&!confirm('Start TCP management connectivity checks to the selected customer CPE?'))return;
  try{
   await api('/assurance/cpe-probes','POST',{
    serviceId:v.serviceId,equipmentId:v.equipmentId,tcpPort:Number(v.tcpPort),
    intervalSeconds:Number(v.intervalSeconds),enabled,confirm:enabled
   });
   form.reset();error.textContent='CPE probe registered.';await reload();
  }catch(e){error.textContent=e.message}
 };
 el.querySelector('#cpe-refresh').onclick=reload;reload();
}
