using Csls.App;

return await RootCommandFactory.Create().Parse(args).InvokeAsync().ConfigureAwait(false);
