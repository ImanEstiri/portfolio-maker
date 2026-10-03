// Hands PDF bytes fetched by the authenticated client to the browser as a
// download. An <a href> to the API endpoint cannot be used: a navigation does
// not carry the Authorization header.
window.cvMakerDownload = (fileName, base64) => {
    const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
    const url = URL.createObjectURL(new Blob([bytes], { type: 'application/pdf' }));

    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();

    // Revoking immediately can race the download in some browsers.
    setTimeout(() => URL.revokeObjectURL(url), 30000);
};
